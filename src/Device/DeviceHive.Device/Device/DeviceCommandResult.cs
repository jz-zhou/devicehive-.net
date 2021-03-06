﻿using System;

namespace DeviceHive.Device
{
    /// <summary>
    /// Represents objects describing DeviceHive command execution result.
    /// </summary>
    public class DeviceCommandResult
    {
        #region Public Properties

        /// <summary>
        /// Gets command execution status.
        /// </summary>
        public string Status { get; private set; }

        /// <summary>
        /// Gets a custom strongly-typed object with the command execution result (optional).
        /// </summary>
        public object Result { get; private set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes device command status.
        /// </summary>
        /// <param name="status">Command execution status.</param>
        public DeviceCommandResult(string status)
        {
            if (string.IsNullOrEmpty(status))
                throw new ArgumentException("Status is null or empty!", "status");

            Status = status;
        }

        /// <summary>
        /// Initializes device command status and result.
        /// </summary>
        /// <param name="status">Command execution status.</param>
        /// <param name="result">A custom strongly-typed object with the command execution result (optional).</param>
        public DeviceCommandResult(string status, object result)
            : this(status)
        {
            Result = result;
        }
        #endregion
    }
}
