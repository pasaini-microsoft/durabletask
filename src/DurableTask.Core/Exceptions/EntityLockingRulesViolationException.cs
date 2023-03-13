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

namespace DurableTask.Core.Exceptions
{
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// Indicates an invalid use of entities in critical sections.
    /// </summary>
    [Serializable]
    public class EntityLockingRulesViolationException : InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public EntityLockingRulesViolationException()
        {
        }

        /// <summary>
        /// Initializes an new instance with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public EntityLockingRulesViolationException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes an new instance with a specified error message.
        ///    and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
        public EntityLockingRulesViolationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance with serialized data.
        /// </summary>
        /// <param name="info">The System.Runtime.Serialization.SerializationInfo that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The System.Runtime.Serialization.StreamingContext that contains contextual information about the source or destination.</param>
        protected EntityLockingRulesViolationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}