﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using DeviceHive.Core.Mapping;
using DeviceHive.Core.MessageLogic;
using DeviceHive.Core.Messaging;
using DeviceHive.Data;
using DeviceHive.Data.Model;
using DeviceHive.WebSockets.API.Filters;
using DeviceHive.WebSockets.API.Subscriptions;
using DeviceHive.WebSockets.Core.ActionsFramework;
using DeviceHive.WebSockets.Core.Network;
using Newtonsoft.Json.Linq;
using Ninject;

namespace DeviceHive.WebSockets.API.Controllers
{
    /// <summary>
    /// <para>
    /// The service allows clients to exchange messages with the DeviceHive server using a single persistent connection.
    /// </para>
    /// <para>
    /// After connection is eshtablished, clients need to authenticate using their login and password or access key,
    /// and then start sending commands to devices using the command/insert message.
    /// As soon as a command is processed by a device, the server sends the command/update message.
    /// </para>
    /// <para>
    /// Clients may also subscribe to device notifications using the notification/subscribe message
    /// and then start receiving server-originated messages about new device notifications.
    /// </para>
    /// </summary>
    public class ClientController : ControllerBase
    {        
        #region Private Fields

        private static readonly DeviceSubscriptionManager _deviceSubscriptionManagerForNotifications = new DeviceSubscriptionManager("DeviceSubscriptions_Notification");
        private static readonly DeviceSubscriptionManager _deviceSubscriptionManagerForCommands = new DeviceSubscriptionManager("DeviceSubscriptions_Command");
        private static readonly CommandSubscriptionManager _commandSubscriptionManager = new CommandSubscriptionManager();
        
        private readonly MessageBus _messageBus;
        private readonly IMessageManager _messageManager;

        #endregion

        #region Constructor

        public ClientController(ActionInvoker actionInvoker,
            DataContext dataContext, JsonMapperManager jsonMapperManager,
            MessageBus messageBus, IMessageManager messageManager) :
            base(actionInvoker, dataContext, jsonMapperManager)
        {
            _messageBus = messageBus;
            _messageManager = messageManager;
        }

        #endregion

        #region Properties

        private User CurrentUser
        {
            get { return (User) Connection.Session["User"]; }
            set { Connection.Session["User"] = value; }
        }

        private AccessKey CurrentAccessKey
        {
            get { return (AccessKey)Connection.Session["AccessKey"]; }
            set { Connection.Session["AccessKey"] = value; }
        }

        #endregion

        #region ControllerBase Members

        public override void CleanupConnection(WebSocketConnectionBase connection)
        {
            base.CleanupConnection(connection);

            _deviceSubscriptionManagerForNotifications.Cleanup(connection);
            _deviceSubscriptionManagerForCommands.Cleanup(connection);
            _commandSubscriptionManager.Cleanup(connection);
        }

        #endregion

        #region Actions Methods

        /// <summary>
        /// Authenticates a client.
        /// Either login and password or accessKey parameters must be passed.
        /// </summary>
        /// <request>
        ///     <parameter name="login" type="string">User login.</parameter>
        ///     <parameter name="password" type="string">User password.</parameter>
        ///     <parameter name="accessKey" type="string">User access key.</parameter>
        /// </request>
        [Action("authenticate")]
        [AuthenticateClient]
        public void Authenticate()
        {
            if (ActionContext.GetParameter("AuthUser") == null)
                throw new WebSocketRequestException("Please specify 'login' and 'password' or 'accessKey'");

            CurrentUser = (User)ActionContext.GetParameter("AuthUser");
            CurrentAccessKey = (AccessKey)ActionContext.GetParameter("AuthAccessKey");

            SendSuccessResponse();
        }

        /// <summary>
        /// Creates new device notification on behalf of device.
        /// </summary>
        /// <param name="deviceGuid">Device unique identifier.</param>
        /// <param name="notification" cref="DeviceNotification">A <see cref="DeviceNotification"/> resource to create.</param>
        /// <response>
        ///     <parameter name="notification" cref="DeviceNotification" mode="OneWayOnly">An inserted <see cref="DeviceNotification"/> resource.</parameter>
        /// </response>
        [Action("notification/insert")]
        [AuthorizeClient(AccessKeyAction = "CreateDeviceNotification")]
        public void InsertDeviceNotification(Guid deviceGuid, JObject notification)
        {
            if (deviceGuid == Guid.Empty)
                throw new WebSocketRequestException("Please specify valid deviceGuid");

            if (notification == null)
                throw new WebSocketRequestException("Please specify notification");

            var device = DataContext.Device.Get(deviceGuid);
            if (device == null || !IsDeviceAccessible(device, "CreateDeviceNotification"))
                throw new WebSocketRequestException("Device not found");

            var notificationEntity = GetMapper<DeviceNotification>().Map(notification);
            notificationEntity.Device = device;
            Validate(notificationEntity);

            DataContext.DeviceNotification.Save(notificationEntity);
            _messageManager.ProcessNotification(notificationEntity);
            _messageBus.Notify(new DeviceNotificationAddedMessage(device.ID, notificationEntity.ID));

            notification = GetMapper<DeviceNotification>().Map(notificationEntity, oneWayOnly: true);
            SendResponse(new JProperty("notification", notification));
        }

        /// <summary>
        /// Creates new device command.
        /// </summary>
        /// <param name="deviceGuid">Target device unique identifier.</param>
        /// <param name="command" cref="DeviceCommand">A <see cref="DeviceCommand"/> resource to create.</param>
        /// <response>
        ///     <parameter name="command" cref="DeviceCommand" mode="OneWayOnly">An inserted <see cref="DeviceCommand"/> resource.</parameter>
        /// </response>
        [Action("command/insert")]
        [AuthorizeClient(AccessKeyAction = "CreateDeviceCommand")]
        public void InsertDeviceCommand(Guid deviceGuid, JObject command)
        {
            if (deviceGuid == Guid.Empty)
                throw new WebSocketRequestException("Please specify valid deviceGuid");

            if (command == null)
                throw new WebSocketRequestException("Please specify command");

            var device = DataContext.Device.Get(deviceGuid);
            if (device == null || !IsDeviceAccessible(device, "CreateDeviceCommand"))
                throw new WebSocketRequestException("Device not found");

            var commandEntity = GetMapper<DeviceCommand>().Map(command);
            commandEntity.Device = device;
            commandEntity.UserID = CurrentUser.ID;
            Validate(commandEntity);

            DataContext.DeviceCommand.Save(commandEntity);
            _commandSubscriptionManager.Subscribe(Connection, commandEntity.ID);
            _messageBus.Notify(new DeviceCommandAddedMessage(device.ID, commandEntity.ID));

            command = GetMapper<DeviceCommand>().Map(commandEntity, oneWayOnly: true);
            SendResponse(new JProperty("command", command));
        }

        /// <summary>
        /// Updates an existing device command on behalf of device.
        /// </summary>
        /// <param name="deviceGuid">Device unique identifier.</param>
        /// <param name="commandId">Device command identifier.</param>
        /// <param name="command" cref="DeviceCommand">A <see cref="DeviceCommand"/> resource to update.</param>
        /// <request>
        ///     <parameter name="command.command" required="false" />
        /// </request>
        [Action("command/update")]
        [AuthorizeClient(AccessKeyAction = "UpdateDeviceCommand")]
        public void UpdateDeviceCommand(Guid deviceGuid, int commandId, JObject command)
        {
            if (deviceGuid == Guid.Empty)
                throw new WebSocketRequestException("Please specify valid deviceGuid");

            if (commandId == 0)
                throw new WebSocketRequestException("Please specify valid commandId");
            
            if (command == null)
                throw new WebSocketRequestException("Please specify command");

            var device = DataContext.Device.Get(deviceGuid);
            if (device == null || !IsDeviceAccessible(device, "UpdateDeviceCommand"))
                throw new WebSocketRequestException("Device not found");

            var commandEntity = DataContext.DeviceCommand.Get(commandId);
            if (commandEntity == null || commandEntity.DeviceID != device.ID)
                throw new WebSocketRequestException("Device command not found");

            GetMapper<DeviceCommand>().Apply(commandEntity, command);
            commandEntity.Device = device;
            Validate(commandEntity);

            DataContext.DeviceCommand.Save(commandEntity);
            _messageBus.Notify(new DeviceCommandUpdatedMessage(device.ID, commandEntity.ID));

            SendSuccessResponse();
        }

        /// <summary>
        /// Subscribes to device notifications.
        /// After subscription is completed, the server will start to send notification/insert messages to the connected user.
        /// </summary>
        /// <param name="timestamp">Timestamp of the last received notification (UTC). If not specified, the server's timestamp is taken instead.</param>
        /// <request>
        ///     <parameter name="deviceGuids" type="array">Array of device unique identifiers to subscribe to. If not specified, the subscription is made to all accessible devices.</parameter>
        /// </request>
        [Action("notification/subscribe")]
        [AuthorizeClient(AccessKeyAction = "GetDeviceNotification")]
        public void SubsrcibeToDeviceNotifications(DateTime? timestamp)
        {
            var devices = GetSubscriptionDevices("GetDeviceNotification").ToArray();

            if (timestamp != null)
                SendInitialNotifications(devices, timestamp);

            foreach (var deviceId in GetSubscriptionDeviceIds(devices))
                _deviceSubscriptionManagerForNotifications.Subscribe(Connection, deviceId);

            SendSuccessResponse();
        }

        /// <summary>
        /// Subscribes to device commands.
        /// After subscription is completed, the server will start to send command/insert messages to the connected user.
        /// </summary>
        /// <param name="timestamp">Timestamp of the last received command (UTC). If not specified, the server's timestamp is taken instead.</param>
        /// <request>
        ///     <parameter name="deviceGuids" type="array">Array of device unique identifiers to subscribe to. If not specified, the subscription is made to all accessible devices.</parameter>
        /// </request>
        [Action("command/subscribe")]
        [AuthorizeClient(AccessKeyAction = "GetDeviceCommand")]
        public void SubsrcibeToDeviceCommands(DateTime? timestamp)
        {
            var devices = GetSubscriptionDevices("GetDeviceCommand").ToArray();

            if (timestamp != null)
                SendInitialCommands(devices, timestamp);

            foreach (var deviceId in GetSubscriptionDeviceIds(devices))
                _deviceSubscriptionManagerForCommands.Subscribe(Connection, deviceId);

            SendSuccessResponse();
        }

        /// <summary>
        /// Unsubscribes from device notifications.
        /// </summary>
        /// <request>
        ///     <parameter name="deviceGuids" type="array">Array of device unique identifiers to unsubscribe from. Keep null to unsubscribe from previously made subscription to all accessible devices.</parameter>
        /// </request>
        [Action("notification/unsubscribe")]
        [AuthorizeClient(AccessKeyAction = "GetDeviceNotification")]
        public void UnsubsrcibeFromDeviceNotifications()
        {
            var devices = GetSubscriptionDevices("GetDeviceNotification").ToArray();
            foreach (var deviceId in GetSubscriptionDeviceIds(devices))
                _deviceSubscriptionManagerForNotifications.Unsubscribe(Connection, deviceId);

            SendSuccessResponse();
        }

        /// <summary>
        /// Unsubscribes from device commands.
        /// </summary>
        /// <request>
        ///     <parameter name="deviceGuids" type="array">Array of device unique identifiers to unsubscribe from. Keep null to unsubscribe from previously made subscription to all accessible devices.</parameter>
        /// </request>
        [Action("command/unsubscribe")]
        [AuthorizeClient(AccessKeyAction = "GetDeviceCommand")]
        public void UnsubsrcibeFromDeviceCommands()
        {
            var devices = GetSubscriptionDevices("GetDeviceCommand").ToArray();
            foreach (var deviceId in GetSubscriptionDeviceIds(devices))
                _deviceSubscriptionManagerForCommands.Unsubscribe(Connection, deviceId);

            SendSuccessResponse();
        }

        /// <summary>
        /// Gets meta-information about the current API.
        /// </summary>
        /// <response>
        ///     <parameter name="info" cref="ApiInfo">The <see cref="ApiInfo"/> resource.</parameter>
        /// </response>
        [Action("server/info")]
        public void ServerInfo()
        {
            var apiInfo = new ApiInfo
            {
                ApiVersion = DeviceHive.Core.Version.ApiVersion,
                ServerTimestamp = DataContext.Timestamp.GetCurrentTimestamp(),
                RestServerUrl = ConfigurationManager.AppSettings["RestServerUrl"]
            };

            SendResponse(new JProperty("info", GetMapper<ApiInfo>().Map(apiInfo)));
        }

        #endregion

        #region Notification Subscription Handling

        /// <summary>
        /// Notifies the user about new device notification.
        /// </summary>
        /// <action>notification/insert</action>
        /// <response>
        ///     <parameter name="deviceGuid" type="guid">Device unique identifier.</parameter>
        ///     <parameter name="notification" cref="DeviceNotification">A <see cref="DeviceNotification"/> resource representing the notification.</parameter>
        /// </response>
        public void HandleDeviceNotification(int deviceId, int notificationId)
        {
            var connections = _deviceSubscriptionManagerForNotifications.GetConnections(deviceId);
            if (connections.Any())
            {
                var notification = DataContext.DeviceNotification.Get(notificationId);
                var device = DataContext.Device.Get(deviceId);

                foreach (var connection in connections)
                    Notify(connection, notification, device);
            }
        }

        private void SendInitialNotifications(Device[] devices, DateTime? timestamp)
        {
            var initialNotificationList = GetInitialNotificationList(Connection);

            lock (initialNotificationList)
            {
                var filter = new DeviceNotificationFilter { Start = timestamp, IsDateInclusive = false };
                var deviceIds = devices.Length == 1 && devices[0] == null ? null : devices.Select(d => d.ID).ToArray();
                var initialNotifications = DataContext.DeviceNotification.GetByDevices(deviceIds, filter)
                    .Where(n => IsDeviceAccessible(n.Device, "GetDeviceNotification")).ToArray();

                foreach (var notification in initialNotifications)
                {
                    initialNotificationList.Add(notification.ID);
                    Notify(Connection, notification, notification.Device, isInitialNotification: true);
                }
            }
        }

        private void Notify(WebSocketConnectionBase connection, DeviceNotification notification, Device device,
            bool isInitialNotification = false)
        {
            if (!isInitialNotification)
            {
                var initialNotificationList = GetInitialNotificationList(connection);
                lock (initialNotificationList)
                {
                    if (initialNotificationList.Contains(notification.ID))
                        return;
                }

                if (!IsDeviceAccessible(connection, device, "GetDeviceNotification"))
                    return;
            }

            connection.SendResponse("notification/insert",
                new JProperty("deviceGuid", device.GUID),
                new JProperty("notification", GetMapper<DeviceNotification>().Map(notification)));
        }

        #endregion

        #region Command Subscription Handling

        /// <summary>
        /// Notifies the user about new device command.
        /// </summary>
        /// <action>command/insert</action>
        /// <response>
        ///     <parameter name="deviceGuid" type="guid">Device unique identifier.</parameter>
        ///     <parameter name="command" cref="DeviceCommand">A <see cref="DeviceCommand"/> resource representing the command.</parameter>
        /// </response>
        public void HandleDeviceCommand(int deviceId, int commandId)
        {
            var connections = _deviceSubscriptionManagerForCommands.GetConnections(deviceId);
            if (connections.Any())
            {
                var command = DataContext.DeviceCommand.Get(commandId);
                var device = DataContext.Device.Get(deviceId);

                foreach (var connection in connections)
                    Notify(connection, command, device);
            }
        }

        private void SendInitialCommands(Device[] devices, DateTime? timestamp)
        {
            var initialCommandList = GetInitialCommandList(Connection);

            lock (initialCommandList)
            {
                var filter = new DeviceCommandFilter { Start = timestamp, IsDateInclusive = false };
                var deviceIds = devices.Length == 1 && devices[0] == null ? null : devices.Select(d => d.ID).ToArray();
                var initialCommands = DataContext.DeviceCommand.GetByDevices(deviceIds, filter)
                    .Where(n => IsDeviceAccessible(n.Device, "GetDeviceCommand")).ToArray();

                foreach (var command in initialCommands)
                {
                    initialCommandList.Add(command.ID);
                    Notify(Connection, command, command.Device, isInitialCommand: true);
                }
            }
        }

        private void Notify(WebSocketConnectionBase connection, DeviceCommand command, Device device,
            bool isInitialCommand = false)
        {
            if (!isInitialCommand)
            {
                var initialCommandList = GetInitialCommandList(connection);
                lock (initialCommandList)
                {
                    if (initialCommandList.Contains(command.ID))
                        return;
                }

                if (!IsDeviceAccessible(connection, device, "GetDeviceCommand"))
                    return;
            }

            connection.SendResponse("command/insert",
                new JProperty("deviceGuid", device.GUID),
                new JProperty("command", GetMapper<DeviceCommand>().Map(command)));
        }

        #endregion

        #region Command Update Subscription Handling

        /// <summary>
        /// Notifies the user about a command has been processed by a device.
        /// These messages are sent only for commands created by the current user within the current connection.
        /// </summary>
        /// <action>command/update</action>
        /// <response>
        ///     <parameter name="command" cref="DeviceCommand">A <see cref="DeviceCommand"/> resource representing the processed command.</parameter>
        /// </response>
        public void HandleCommandUpdate(int commandId)
        {
            var connections = _commandSubscriptionManager.GetConnections(commandId);
            if (connections.Any())
            {
                var command = DataContext.DeviceCommand.Get(commandId);
                foreach (var connection in connections)
                {
                    connection.SendResponse("command/update",
                        new JProperty("command", GetMapper<DeviceCommand>().Map(command)));
                }
            }
        }

        #endregion

        #region Private Methods

        private bool IsDeviceAccessible(Device device, string accessKeyAction)
        {
            return IsDeviceAccessible(Connection, device, accessKeyAction);
        }
        
        private bool IsDeviceAccessible(WebSocketConnectionBase connection, Device device, string accessKeyAction)
        {
            var user = (User)connection.Session["User"];
            if (user == null)
                return false;

            if (user.Role != (int)UserRole.Administrator)
            {
                if (device.NetworkID == null)
                    return false;

                var userNetworks = (List<UserNetwork>)connection.Session["UserNetworks"];
                if (userNetworks == null)
                {
                    userNetworks = DataContext.UserNetwork.GetByUser(user.ID);
                    connection.Session["UserNetworks"] = userNetworks;
                }

                if (!userNetworks.Any(un => un.NetworkID == device.NetworkID))
                    return false;
            }

            // check if access key permissions are sufficient
            var accessKey = (AccessKey)connection.Session["AccessKey"];
            return accessKey == null || accessKey.Permissions.Any(p =>
                p.IsActionAllowed(accessKeyAction) && p.IsAddressAllowed(connection.Host) &&
                p.IsNetworkAllowed(device.NetworkID ?? 0) && p.IsDeviceAllowed(device.GUID.ToString()));
        }

        private IEnumerable<Device> GetSubscriptionDevices(string accessKeyAction)
        {
            var deviceGuids = ParseDeviceGuids();
            if (deviceGuids == null)
            {
                yield return null;
                yield break;
            }

            foreach (var deviceGuid in deviceGuids)
            {
                var device = DataContext.Device.Get(deviceGuid);
                if (device == null || !IsDeviceAccessible(device, accessKeyAction))
                    throw new WebSocketRequestException("Invalid deviceGuid: " + deviceGuid);

                yield return device;
            }
        }

        private IEnumerable<int?> GetSubscriptionDeviceIds(IEnumerable<Device> devices)
        {
            foreach (var device in devices)
                yield return device != null ? (int?)device.ID : null;
        }

        private IEnumerable<Guid> ParseDeviceGuids()
        {
            if (ActionContext.Request == null)
                return null;

            var deviceGuids = ActionContext.Request["deviceGuids"];
            if (deviceGuids == null)
                return null;

            var deviceGuidsArray = deviceGuids as JArray;
            if (deviceGuidsArray != null)
                return deviceGuidsArray.Select(t => (Guid)t).ToArray();

            return new[] { (Guid)deviceGuids };
        }

        private ISet<int> GetInitialNotificationList(WebSocketConnectionBase connection)
        {
            return (ISet<int>)connection.Session.GetOrAdd("InitialNotifications", () => new HashSet<int>());
        }

        private ISet<int> GetInitialCommandList(WebSocketConnectionBase connection)
        {
            return (ISet<int>)connection.Session.GetOrAdd("InitialCommands", () => new HashSet<int>());
        }

        #endregion
    }
}