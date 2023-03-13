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

namespace DurableTask.Core.Entities.StateFormat
{
    using System.Collections.Generic;
    using DurableTask.Core.Entities.EventFormat;
    using Newtonsoft.Json;

    /// <summary>
    /// The persisted state of an entity scheduler, as handed forward between ContinueAsNew instances.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    internal class SchedulerState
    {
        /// <summary>
        /// Whether this entity exists or not.
        /// </summary>
        [JsonProperty(PropertyName = "exists", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool EntityExists { get; set; }

        /// <summary>
        /// The last serialized entity state.
        /// </summary>
        [JsonProperty(PropertyName = "state", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string EntityState { get; set; }

        /// <summary>
        /// The queue of waiting operations, or null if none.
        /// </summary>
        [JsonProperty(PropertyName = "queue", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public Queue<RequestMessage> Queue { get; private set; }

        /// <summary>
        /// The instance id of the orchestration that currently holds the lock of this entity.
        /// </summary>
        [JsonProperty(PropertyName = "lockedBy", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string LockedBy { get; set; }

        /// <summary>
        /// Whether processing on this entity is currently suspended.
        /// </summary>
        [JsonProperty(PropertyName = "suspended", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Suspended { get; set; }

        /// <summary>
        /// The metadata used for reordering and deduplication of messages sent to entities.
        /// </summary>
        [JsonProperty(PropertyName = "sorter", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public MessageSorter MessageSorter { get; set; } = new MessageSorter();

        [JsonIgnore]
        public bool IsEmpty => !EntityExists && (Queue == null || Queue.Count == 0) && LockedBy == null;

        internal void Enqueue(RequestMessage operationMessage)
        {
            if (Queue == null)
            {
                Queue = new Queue<RequestMessage>();
            }

            Queue.Enqueue(operationMessage);
        }

        internal void PutBack(Queue<RequestMessage> messages)
        {
            if (Queue != null)
            {
                foreach (var message in Queue)
                {
                    messages.Enqueue(message);
                }
            }

            Queue = messages;
        }

        internal bool MayDequeue()
        {
            return Queue != null
                && Queue.Count > 0
                && (LockedBy == null || LockedBy == Queue.Peek().ParentInstanceId);
        }

        internal RequestMessage Dequeue()
        {
            var result = Queue.Dequeue();

            if (Queue.Count == 0)
            {
                Queue = null;
            }

            return result;
        }

        public override string ToString()
        {
            return $"exists={EntityExists} queue.count={(Queue != null ? Queue.Count : 0)}";
        }
    }
}