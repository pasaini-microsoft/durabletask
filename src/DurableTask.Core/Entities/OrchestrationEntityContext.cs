﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------
namespace DurableTask.Core.Entities
{
    using DurableTask.Core.Common;
    using DurableTask.Core.Entities.EventFormat;
    using DurableTask.Core.Entities.OperationFormat;
    using DurableTask.Core.Entities.StateFormat;
    using DurableTask.Core.History;
    using DurableTask.Core.Serializing;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// Tracks the entity-related state of an orchestration. 
    /// Tracks and validates the synchronization state.
    /// </summary>
    public class OrchestrationEntityContext
    {
        private readonly string instanceId;
        private readonly string executionId;
        private readonly OrchestrationContext innerContext;
        private readonly Dictionary<Guid, IEntityResponseContinuation> pending;
        private readonly MessageSorter messageSorter;

        private Guid? criticalSectionId;
        private EntityId[] criticalSectionLocks;
        private bool lockAcquisitionPending;
        private HashSet<EntityId> availableLocks;

        /// <summary>
        /// Constructs an OrchestrationEntityContext.
        /// </summary>
        /// <param name="instanceId">The instance id.</param>
        /// <param name="executionId">The execution id.</param>
        /// <param name="innerContext">The inner context.</param>
        public OrchestrationEntityContext(
            string instanceId,
            string executionId,
            OrchestrationContext innerContext)
        {
            this.instanceId = instanceId;
            this.executionId = executionId;
            this.innerContext = innerContext;
            this.pending = new Dictionary<Guid, IEntityResponseContinuation>();
            this.messageSorter = new MessageSorter();
        }

        /// <summary>
        /// Whether this orchestration is currently inside a critical section.
        /// </summary>
        public bool IsInsideCriticalSection => this.criticalSectionId != null;   

        /// <summary>
        /// Enumerate all the entities that are available for calling from within a critical section. 
        /// This set contains all the entities that were locked prior to entering the critical section,
        /// and for which there is not currently an operation call pending.
        /// </summary>
        /// <returns>An enumeration of all the currently available entities.</returns>
        public IEnumerable<(string entityName, string entityKey)> GetAvailableEntities()
        {
            if (this.IsInsideCriticalSection)
            {
                foreach(var e in this.availableLocks)
                {
                    yield return (e.EntityName, e.EntityKey);
                }
            }
        }

        /// <summary>
        /// Check that a suborchestration is a valid transition in the current state.
        /// </summary>
        /// <param name="errorMessage">The error message, if it is not valid, or null otherwise</param>
        /// <returns>whether the transition is valid </returns>
        public bool ValidateSuborchestrationTransition(out string errorMessage)
        {
            if (this.IsInsideCriticalSection)
            {
                errorMessage = "While holding locks, cannot call suborchestrators.";
                return false;
            }

            errorMessage = null;
            return true;
        }

        /// <summary>
        /// Check that acquire is a valid transition in the current state.
        /// </summary>
        /// <param name="oneWay">Whether this is a signal or a call.</param>
        /// <param name="targetInstanceId">The target instance id.</param>
        /// <param name="errorMessage">The error message, if it is not valid, or null otherwise</param>
        /// <returns>whether the transition is valid </returns>
        public bool ValidateOperationTransition(string targetInstanceId, bool oneWay, out string errorMessage)
        {
            if (this.IsInsideCriticalSection)
            {
                var lockToUse = EntityId.GetEntityIdFromInstanceId(targetInstanceId);
                if (oneWay)
                {
                    if (this.criticalSectionLocks.Contains(lockToUse))
                    {
                        errorMessage = "Must not signal a locked entity from a critical section.";
                        return false;
                    }
                }
                else
                {
                    if (!this.availableLocks.Remove(lockToUse))
                    {
                        if (this.lockAcquisitionPending)
                        {
                            errorMessage = "Must await the completion of the lock request prior to calling any entity.";
                            return false;
                        }
                        if (this.criticalSectionLocks.Contains(lockToUse))
                        {
                            errorMessage = "Must not call an entity from a critical section while a prior call to the same entity is still pending.";
                            return false;
                        }
                        else
                        {
                            errorMessage = "Must not call an entity from a critical section if it is not one of the locked entities.";
                            return false;
                        }
                    }
                }
            }

            errorMessage = null;
            return true;
        }

        /// <summary>
        /// Check that acquire is a valid transition in the current state.
        /// </summary>
        /// <param name="errorMessage">The error message, if it is not valid, or null otherwise</param>
        /// <returns>whether the transition is valid </returns>
        public bool ValidateAcquireTransition(out string errorMessage)
        {
            if (this.IsInsideCriticalSection)
            {
                errorMessage = "Must not enter another critical section from within a critical section.";
                return false;
            }

            errorMessage = null;
            return true;
        }

        /// <summary>
        /// Called after an operation call within a critical section completes.
        /// </summary>
        /// <param name="targetInstanceId"></param>
        public void RecoverLockAfterCall(string targetInstanceId)
        {
            if (this.IsInsideCriticalSection)
            {
                var lockToUse = EntityId.GetEntityIdFromInstanceId(targetInstanceId);
                this.availableLocks.Add(lockToUse);
            }
        }

        /// <summary>
        /// Get release messages for all locks in the critical section, and release them
        /// </summary>
        public IEnumerable<(OrchestrationInstance target, string eventName, object eventContent)> EmitLockReleaseMessages()
        {
            if (this.IsInsideCriticalSection)
            {
                var message = new ReleaseMessage()
                {
                    ParentInstanceId = instanceId,
                    Id = this.criticalSectionId.Value.ToString(),
                };

                foreach (var entityId in this.criticalSectionLocks)
                {
                    var instance = new OrchestrationInstance() { InstanceId = EntityId.GetInstanceIdFromEntityId(entityId) };
                    var jmessage = JObject.FromObject(message, Serializer.InternalSerializer);
                    yield return (instance, EntityMessageEventNames.ReleaseMessageEventName, jmessage);
                }

                this.criticalSectionLocks = null;
                this.availableLocks = null;
                this.criticalSectionId = null;
            }
        }

        /// <summary>
        /// Creates a request message to be sent to an entity.
        /// </summary>
        /// <param name="target">The target entity.</param>
        /// <param name="operationName">The name of the operation.</param>
        /// <param name="oneWay">If true, this is a signal, otherwise it is a call.</param>
        /// <param name="operationId">A unique identifier for this request.</param>
        /// <param name="scheduledTimeUtc">A time for which to schedule the delivery, or null if this is not a scheduled message</param>
        /// <param name="input">The operation input</param>
        /// <returns></returns>
        public (string eventName, object eventContent) EmitRequestMessage(
            OrchestrationInstance target,
            string operationName,
            bool oneWay,
            Guid operationId,
            (DateTime original, DateTime capped)? scheduledTimeUtc,
            string input)
        {
            var request = new RequestMessage()
            {
                ParentInstanceId = this.instanceId,
                ParentExecutionId = this.executionId,
                Id = operationId,
                IsSignal = oneWay,
                Operation = operationName,
                ScheduledTime = scheduledTimeUtc?.original,
                Input = input,
            };

            this.AdjustOutgoingMessage(target.InstanceId, request, scheduledTimeUtc?.capped, out string eventName);

            // we pre-serialize to JObject so we can avoid exposure to application-specific serialization settings 
            var jrequest = JObject.FromObject(request, Serializer.InternalSerializer);

            return (eventName, jrequest);
        }

        /// <summary>
        /// Creates an acquire message to be sent to an entity.
        /// </summary>
        /// <param name="lockRequestId">A unique request id.</param>
        /// <param name="entities">All the entities that are to be acquired.</param>
        /// <returns></returns>
        public (OrchestrationInstance target, string eventName, object eventContent) EmitAcquireMessage(Guid lockRequestId, EntityId[] entities)
        {
            // All the entities in entity[] need to be locked, but to avoid deadlock, the locks have to be acquired
            // sequentially, in order. So, we send the lock request to the first entity; when the first lock
            // is granted by the first entity, the first entity will forward the lock request to the second entity,
            // and so on; after the last entity grants the last lock, a response is sent back here.

            // acquire the locks in a globally fixed order to avoid deadlocks
            Array.Sort(entities);

            // remove duplicates if necessary. Probably quite rare, so no need to optimize more.
            for (int i = 0; i < entities.Length - 1; i++)
            {
                if (entities[i].Equals(entities[i + 1]))
                {
                    entities = entities.Distinct().ToArray();
                    break;
                }
            }

            // send lock request to first entity in the lock set
            var target = new OrchestrationInstance() { InstanceId = EntityId.GetInstanceIdFromEntityId(entities[0]) };
            var request = new RequestMessage()
            {
                Id = lockRequestId,
                ParentInstanceId = this.instanceId,
                ParentExecutionId = this.executionId,
                LockSet = entities,
                Position = 0,
            };

            this.criticalSectionId = lockRequestId;
            this.criticalSectionLocks = entities;
            this.lockAcquisitionPending = true;

            this.AdjustOutgoingMessage(target.InstanceId, request, null, out string eventName);

            // we pre-serialize to JObject so we can avoid exposure to application-specific serialization settings
            var jrequest = JObject.FromObject(request, Serializer.InternalSerializer);

            return (target, eventName, jrequest);
        }

        /// <summary>
        /// Called when a response to the acquire message is received from the last entity.
        /// </summary>
        /// <param name="result">The result returned.</param>
        /// <param name="criticalSectionId">The guid for the lock operation</param>
        public void CompleteAcquire(OperationResult result, Guid criticalSectionId)
        {
            this.availableLocks = new HashSet<EntityId>(this.criticalSectionLocks);
            this.lockAcquisitionPending = false;
        }

        internal void AdjustOutgoingMessage(string instanceId, RequestMessage requestMessage, DateTime? cappedTime, out string eventName)
        {
            if (requestMessage.ScheduledTime.HasValue)
            {
                eventName = EntityMessageEventNames.ScheduledRequestMessageEventName(cappedTime.Value);
            }
            else
            {
                this.messageSorter.LabelOutgoingMessage(
                    requestMessage,
                    instanceId,
                    this.innerContext.CurrentUtcDateTime,
                    this.innerContext.EntityBackendInformation.EntityMessageReorderWindow);

                eventName = EntityMessageEventNames.RequestMessageEventName;
            }
        }

        /// <summary>
        /// Extracts the operation result from an event that represents an entity response.
        /// </summary>
        /// <param name="eventContent"></param>
        /// <returns></returns>
        public OperationResult DeserializeEntityResponseEvent(string eventContent)
        {
            var responseMessage = new ResponseMessage();

            // for compatibility, we deserialize in a way that is resilient to any typename presence/absence/mismatch
            try
            {
                // restore the scheduler state from the input
                JsonConvert.PopulateObject(eventContent, responseMessage, Serializer.InternalSerializerSettings);
            }
            catch (Exception exception)
            {
                throw new EntitySchedulerException("Failed to deserialize entity response.", exception);
            }

            return new OperationResult()
            {
                Result = responseMessage.Result,
                ErrorMessage = responseMessage.ErrorMessage,
                FailureDetails = responseMessage.FailureDetails,
            };
        }


        private interface IEntityResponseContinuation
        {
            void DeliverResult(OperationResult operationResult, EventRaisedEvent eventRaisedEvent, TaskOrchestrationContext taskOrchestrationContext);
        }

        private class OperationContinuation<T> : TaskCompletionSource<T>, IEntityResponseContinuation
        {
            readonly private int taskId;
            readonly private string instanceId;

            public OperationContinuation(int taskId, string instanceId)
            {
                this.taskId = taskId;
                this.instanceId = instanceId;
            }

            public void DeliverResult(OperationResult operationResult, EventRaisedEvent eventRaisedEvent, TaskOrchestrationContext taskOrchestrationContext)
            {
                if (taskOrchestrationContext.EntityContext.IsInsideCriticalSection)
                {
                    // the lock is available again now that the entity call returned
                    taskOrchestrationContext.EntityContext.RecoverLockAfterCall(this.instanceId);
                }

                if (operationResult.ErrorMessage == null)
                {
                    taskOrchestrationContext.HandleEntityOperationCompletedEvent<T>(eventRaisedEvent, this.taskId, this.instanceId, operationResult, this);
                }
                else
                {
                    taskOrchestrationContext.HandleEntityOperationFailedEvent<T>(eventRaisedEvent, this.taskId, this.instanceId, operationResult, this);
                }
            }
        }

        private class LockAcquisitionContinuation : TaskCompletionSource<IDisposable>, IEntityResponseContinuation
        {
            readonly private Guid criticalSectionId;
            readonly private int taskId;

            public LockAcquisitionContinuation(Guid lockRequestId, int taskId)
            {
                this.criticalSectionId = lockRequestId;
                this.taskId = taskId;
            }

            public void DeliverResult(OperationResult operationResult, EventRaisedEvent eventRaisedEvent, TaskOrchestrationContext taskOrchestrationContext)
            {
                taskOrchestrationContext.EntityContext.CompleteAcquire(operationResult, this.criticalSectionId);
                this.SetResult(new LockReleaser(taskOrchestrationContext, criticalSectionId));
            }             
        }

        private class LockReleaser : IDisposable
        {
            private readonly TaskOrchestrationContext context;
            private readonly Guid criticalSectionId;

            public LockReleaser(TaskOrchestrationContext context, Guid criticalSectionId)
            {
                this.context = context;
                this.criticalSectionId = criticalSectionId; 
            }

            public void Dispose()
            {
                this.context.ExitCriticalSection();
            }
        }

        internal Task<T> WaitForOperationResponseAsync<T>(Guid operationId, int taskId, string instanceId)
        {
            var promise = new OperationContinuation<T>(taskId, instanceId);
            lock (this.pending)
            {
                this.pending.Add(operationId, promise);
            }
            return promise.Task;
        }

        internal Task<IDisposable> WaitForLockResponseAsync(Guid criticalSectionId, int taskId)
        {
            var promise = new LockAcquisitionContinuation(criticalSectionId, taskId);
            lock (this.pending)
            {
                this.pending.Add(criticalSectionId, promise);
            }
            return promise.Task;
        }

        internal bool HandledAsEntityResponse(EventRaisedEvent evt, TaskOrchestrationContext taskOrchestrationContext)
        {
            Guid operationId;
            if (!Guid.TryParse(evt.Name, out operationId))
            {
                return false;
            }

            IEntityResponseContinuation completionSource;
            lock (this.pending)
            {
                if (!this.pending.TryGetValue(operationId, out completionSource))
                {
                    return false;
                }
                this.pending.Remove(operationId);
            }

            var operationResult = this.DeserializeEntityResponseEvent(evt.Input);
            completionSource.DeliverResult(operationResult, evt, taskOrchestrationContext);
          
            return true;
        }
    }
}