﻿using System;
using DeviceHive.Test.Stubs;
using DeviceHive.WebSockets.Controllers;
using Newtonsoft.Json.Linq;
using Ninject;

namespace DeviceHive.Test.WebSocketsTest.Utils
{
    public class ClientControllerWrapper : ControllerWrapperBase<ClientController>
    {
        public ClientControllerWrapper(StubWebSocketServer server, IKernel kernel) :
            base(server, kernel, "/client")
        {
        }

        public JObject Authenticate(StubWebSocketConnection connection, string login, string password)
        {
            return InvokeAction(connection, "authenticate", new JObject(
                new JProperty("login", login),
                new JProperty("password", password)));
        }

        public JObject InsertDeviceCommand(StubWebSocketConnection connection,
            Guid deviceGuid, JObject command)
        {
            return InvokeAction(connection, "command/insert", new JObject(
                new JProperty("deviceGuid", deviceGuid),
                new JProperty("command", command)));
        }
    }
}