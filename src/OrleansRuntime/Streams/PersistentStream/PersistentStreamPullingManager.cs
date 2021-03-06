﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.Streams
{
    internal class PersistentStreamPullingManager : SystemTarget, IPersistentStreamPullingManager, IStreamQueueBalanceListener
    {
        private readonly Dictionary<QueueId, PersistentStreamPullingAgent> queuesToAgentsMap;
        private readonly string streamProviderName;
        private readonly IStreamProviderRuntime providerRuntime;

        private readonly TimeSpan queueGetPeriod;
        private readonly TimeSpan initQueueTimeout;
        private readonly AsyncSerialExecutor nonReentrancyGuarantor; // for non-reentrant execution of queue change notifications.
        private readonly Logger logger;

        private int latestRingNotificationSequenceNumber;
        private IQueueAdapter queueAdapter;
        private readonly IStreamQueueBalancer queueBalancer;

        internal PersistentStreamPullingManager(
            GrainId id, 
            string strProviderName, 
            IStreamProviderRuntime runtime,
            IStreamQueueBalancer streamQueueBalancer,
            TimeSpan queueGetPeriod, 
            TimeSpan initQueueTimeout)
            : base(id, runtime.ExecutingSiloAddress)
        {
            if (string.IsNullOrWhiteSpace(strProviderName))
            {
                throw new ArgumentNullException("strProviderName");
            }
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime", "IStreamProviderRuntime runtime reference should not be null");
            }
            if (streamQueueBalancer == null)
            {
                throw new ArgumentNullException("streamQueueBalancer", "IStreamQueueBalancer streamQueueBalancer reference should not be null");
            }

            queuesToAgentsMap = new Dictionary<QueueId, PersistentStreamPullingAgent>();
            streamProviderName = strProviderName;
            providerRuntime = runtime;
            this.queueGetPeriod = queueGetPeriod;
            this.initQueueTimeout = initQueueTimeout;
            nonReentrancyGuarantor = new AsyncSerialExecutor();
            latestRingNotificationSequenceNumber = 0;
            queueBalancer = streamQueueBalancer;

            logger = providerRuntime.GetLogger(GetType().Name + "-" + streamProviderName);
            logger.Info((int)ErrorCode.PersistentStreamPullingManager_01, "Created {0} for Stream Provider {1}.", GetType().Name, streamProviderName);

            IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS, strProviderName), () => queuesToAgentsMap.Count);
        }

        public Task Initialize(Immutable<IQueueAdapter> qAdapter)
        {
            if (qAdapter.Value == null) throw new ArgumentNullException("qAdapter", "Init: queueAdapter should not be null");

            logger.Info((int)ErrorCode.PersistentStreamPullingManager_02, "Init.");

            // Remove cast once we cleanup
            queueAdapter = qAdapter.Value;

            var meAsQueueBalanceListener = StreamQueueBalanceListenerFactory.Cast(this.AsReference());
            queueBalancer.SubscribeToQueueDistributionChangeEvents(meAsQueueBalanceListener);

            List<QueueId> myQueues = queueBalancer.GetMyQueues().ToList();
            logger.Info((int)ErrorCode.PersistentStreamPullingManager_03, ToString(myQueues));

            return AddNewQueues(myQueues, true);
        }

        #region chance to queue distribution

        /// <summary>
        /// Actions to take when the queue distribution changes due to a failure or a join.
        /// Since this pulling manager is system target and queue distribution change notifications
        /// are delivered to it as grain method calls, notifications are not reentrant. To simplify
        /// notification handling we execute them serially, in a non-reentrant way.  We also supress
        /// and don't execute an older notification if a newer one was already delivered.
        /// </summary>
        public Task QueueDistributionChangeNotification()
        {
            latestRingNotificationSequenceNumber++;
            int notificationSeqNumber = latestRingNotificationSequenceNumber;
            logger.Info((int)ErrorCode.PersistentStreamPullingManager_04,
                "Got QueueChangeNotification number {0} from the queue balancer.", notificationSeqNumber);

            return nonReentrancyGuarantor.SubmitNext(() =>
            {
                // skip execution of an older/previous notification since already got a newer range update notification.
                if (notificationSeqNumber < latestRingNotificationSequenceNumber)
                {
                    logger.Info((int)ErrorCode.PersistentStreamPullingManager_05,
                        "Skipping execution of QueueChangeNotification number {0} from the queue allocator since already received a later notification " +
                        "(already have notification number {1}).",
                        notificationSeqNumber, latestRingNotificationSequenceNumber);
                    return TaskDone.Done;
                }
                return QueueDistributionChangeNotification(notificationSeqNumber);
            });
        }

        private async Task QueueDistributionChangeNotification(int notificationSeqNumber)
        {
            List<QueueId> currentQueues = queueBalancer.GetMyQueues().ToList();
            logger.Info((int)ErrorCode.PersistentStreamPullingManager_06,
                "Executing QueueChangeNotification number {0} from the queue allocator. Current queues: {1}",
                notificationSeqNumber, ToString(currentQueues));

            Task t1 = AddNewQueues(currentQueues, false);
            Task t2 = RemoveQueues(currentQueues);
            await Task.WhenAll(t1, t2);
        }

        /// <summary>
        /// Take responsibility for a set of new queues that were assigned to me via a new range.
        /// We first create one pulling agent for every new queue and store them in our internal data structure, then try to initialize the agents.
        /// ERROR HANDLING:
        ///     The responsibility to handle initialization and shutdown failures is inside the Agents code.
        ///     The manager will call Initialize once and log an error. It will not call initialize again and will assume initialization has succeeded.
        ///     Same applies to shutdown.
        /// </summary>
        /// <param name="myQueues"></param>
        /// <param name="failOnInit"></param>
        /// <returns></returns>
        private async Task AddNewQueues(IEnumerable<QueueId> myQueues, bool failOnInit)
        {
            // Create agents for queues in range that we don't yet have.
            // First create them and store in local queuesToAgentsMap.
            // Only after that Initialize them all.
            var agents = new List<PersistentStreamPullingAgent>();
            foreach (var queueId in myQueues.Where(queueId => !queuesToAgentsMap.ContainsKey(queueId)))
            {
                try
                {
                    var agentId = GrainId.NewSystemTargetGrainIdByTypeCode(Constants.PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE);
                    var agent = new PersistentStreamPullingAgent(agentId, streamProviderName, providerRuntime, queueId, queueGetPeriod, initQueueTimeout);
                    providerRuntime.RegisterSystemTarget(agent);
                    queuesToAgentsMap.Add(queueId, agent);
                    agents.Add(agent);
                }
                catch (Exception exc)
                {
                    logger.Error((int)ErrorCode.PersistentStreamPullingManager_07, String.Format("Exception while creating PersistentStreamPullingAgent."), exc);
                    // What should we do? This error is not recoverable and considered a bug. But we don't want to bring the silo down.
                    // If this is when silo is starting and agent is initializing, fail the silo startup. Otherwise, just swallow to limit impact on other receivers.
                    if (failOnInit) throw;
                }
            }

            try
            {
                var initTasks = new List<Task>();
                foreach (var agent in agents)
                {
                    // Init the agent only after it was registered locally.
                    var agentGrainRef = PersistentStreamPullingAgentFactory.Cast(agent.AsReference());
                    // Need to call it as a grain reference.
                    var task = OrleansTaskExtentions.SafeExecute(() => agentGrainRef.Initialize(((IQueueAdapter)queueAdapter).AsImmutable()));
                    task = task.LogException(logger, ErrorCode.PersistentStreamPullingManager_08, String.Format("PersistentStreamPullingAgent {0} failed to Initialize.", agent.QueueId));
                    initTasks.Add(task);
                }
                await Task.WhenAll(initTasks);
            }
            catch (Exception)
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Initialize. No need to log again.
            }
            logger.Info((int)ErrorCode.PersistentStreamPullingManager_09, "Took {0} new queues under my responsibility: {1}", agents.Count,
                Utils.EnumerableToString(agents, agent => agent.QueueId.ToStringWithHashCode()));
        }

        private async Task RemoveQueues(IEnumerable<QueueId> myQueues)
        {
            // Stop the agents that for queues that are not in my range anymore.
            List<QueueId> queuesToRemove = queuesToAgentsMap.Keys.Where(queueId => !myQueues.Contains(queueId)).ToList();
            var agents = new List<PersistentStreamPullingAgent>(queuesToRemove.Count);
            logger.Info((int)ErrorCode.PersistentStreamPullingManager_10, "Removing {0} agents from my responsibility: {1}", queuesToRemove.Count, Utils.EnumerableToString(queuesToRemove, q => q.ToStringWithHashCode()));
            
            var removeTasks = new List<Task>();
            foreach (var queueId in queuesToRemove)
            {
                PersistentStreamPullingAgent agent;
                if (!queuesToAgentsMap.TryGetValue(queueId, out agent)) continue;

                agents.Add(agent);
                queuesToAgentsMap.Remove(queueId);
                var agentGrainRef = PersistentStreamPullingAgentFactory.Cast(agent.AsReference());
                var task = OrleansTaskExtentions.SafeExecute(agentGrainRef.Shutdown);
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingManager_11,
                    String.Format("PersistentStreamPullingAgent {0} failed to Shutdown.", agent.QueueId));
                removeTasks.Add(task);
            }
            try
            {
                await Task.WhenAll(removeTasks);
            }
            catch (Exception)
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Shutdown. No need to log again.
            }

            foreach (var agent in agents)
            {
                try
                {
                    providerRuntime.UnRegisterSystemTarget(agent);
                }
                catch (Exception exc)
                {
                    logger.Info((int)ErrorCode.PersistentStreamPullingManager_12, 
                        "Exception while UnRegisterSystemTarget of PersistentStreamPullingAgent {0}. Ignoring. Exc.Message = {1}.", agent.GrainId, exc.Message);
                }
            }
        }

        #endregion

        private string ToString(IReadOnlyCollection<QueueId> myQueues)
        {
            return String.Format("I am now responsible for {0} queues: {1}.",
                myQueues.Count, EnumerateQueuesToString(myQueues));
        }

        private string EnumerateQueuesToString(IReadOnlyCollection<QueueId> myQueues)
        {
            return Utils.EnumerableToString(myQueues, q => q.ToStringWithHashCode());
        }
    }
}