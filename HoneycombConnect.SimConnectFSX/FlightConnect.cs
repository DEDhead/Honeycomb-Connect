﻿using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HoneycombConnect.SimConnectFSX
{
    public class FlightConnect
    {
        private const int StatusDelayMilliseconds = 500;

        public event EventHandler<PlaneStatusUpdatedEventArgs> PlaneStatusUpdated;

        public event EventHandler Closed;

        // User-defined win32 event
        const int WM_USER_SIMCONNECT = 0x0402;
        private readonly ILogger<FlightConnect> logger;

        public IntPtr Handle { get; private set; }

        private SimConnect simconnect = null;
        private CancellationTokenSource cts = null;
        private PlaneStatusStruct? currentStatus;

        public FlightConnect(ILogger<FlightConnect> logger)
        {
            this.logger = logger;
        }

        // Simconnect client will send a win32 message when there is 
        // a packet to process. ReceiveMessage must be called to
        // trigger the events. This model keeps simconnect processing on the main thread.
        public IntPtr HandleSimConnectEvents(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam, ref bool isHandled)
        {
            isHandled = false;

            switch (message)
            {
                case WM_USER_SIMCONNECT:
                    {
                        if (simconnect != null)
                        {
                            try
                            {
                                this.simconnect.ReceiveMessage();
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message == "0xC000014B")
                                {
                                    CloseConnection();
                                }
                                else
                                {
                                    RecoverFromError();
                                }
                            }

                            isHandled = true;
                        }
                    }
                    break;

                default:
                    break;
            }

            return IntPtr.Zero;
        }

        // Set up the SimConnect event handlers
        public void Initialize(IntPtr Handle)
        {
            simconnect = new SimConnect("Honeycomb Connect", Handle, WM_USER_SIMCONNECT, null, 0);

            // listen to connect and quit msgs
            simconnect.OnRecvOpen += new SimConnect.RecvOpenEventHandler(simconnect_OnRecvOpen);
            simconnect.OnRecvQuit += new SimConnect.RecvQuitEventHandler(simconnect_OnRecvQuit);

            // listen to exceptions
            simconnect.OnRecvException += simconnect_OnRecvException;

            RegisterPlaneStatusDefinition();
            simconnect.OnRecvSimobjectDataBytype += simconnect_OnRecvSimobjectDataBytypeAsync;

            simconnect.SubscribeToSystemEvent(EVENTS.PAUSE, "Pause");
            simconnect.OnRecvEvent += simconnect_OnRecvEvent;

            simconnect.MapClientEventToSimEvent(EVENTS.BEACON_TOGGLE, "TOGGLE_BEACON_LIGHTS");
            simconnect.MapClientEventToSimEvent(EVENTS.LANDING_LIGHTS_TOGGLE, "LANDING_LIGHTS_TOGGLE");
            simconnect.MapClientEventToSimEvent(EVENTS.TAXI_TOGGLE, "TOGGLE_TAXI_LIGHTS");
            simconnect.MapClientEventToSimEvent(EVENTS.NAV_TOGGLE, "TOGGLE_NAV_LIGHTS");
            simconnect.MapClientEventToSimEvent(EVENTS.STROBE_ON, "STROBES_ON");
            simconnect.MapClientEventToSimEvent(EVENTS.STROBE_OFF, "STROBES_OFF");

            simconnect.MapClientEventToSimEvent(EVENTS.MASTER_ALT_TOGGLE, "TOGGLE_MASTER_ALTERNATOR");
            simconnect.MapClientEventToSimEvent(EVENTS.MASTER_BATTERY_TOGGLE, "TOGGLE_MASTER_BATTERY");
            //simconnect.MapClientEventToSimEvent(EVENTS.MASTER_AVIONICS_TOGGLE, "TOGGLE_AVIONICS_MASTER");
            simconnect.MapClientEventToSimEvent(EVENTS.MASTER_AVIONICS_SET, "AVIONICS_MASTER_SET");
        }

        public void CloseConnection()
        {
            logger.LogInformation("Flight Simulator has exited");
            Closed?.Invoke(this, new EventArgs());
            try
            {
                cts?.Cancel();
                cts = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"Cannot cancel request loop! Error: {ex.Message}");
            }
            try
            {
                if (simconnect != null)
                {
                    // Dispose serves the same purpose as SimConnect_Close()
                    simconnect.Dispose();
                    simconnect = null;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Cannot unsubscribe events! Error: {ex.Message}");
            }
        }

        private void RegisterPlaneStatusDefinition()
        {
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "LIGHT BEACON",
                "bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "LIGHT LANDING",
                "bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "LIGHT TAXI",
                "bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "LIGHT NAV",
                "bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "LIGHT STROBE",
                "bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);

            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "GENERAL ENG MASTER ALTERNATOR:1",
                "Bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "ELECTRICAL MASTER BATTERY",
                "Bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            simconnect.AddToDataDefinition(DEFINITIONS.PlaneStatus,
                "AVIONICS MASTER SWITCH",
                "Bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);

            simconnect.RegisterDataDefineStruct<PlaneStatusStruct>(DEFINITIONS.PlaneStatus);
        }

        public void BeaconSet(bool on)
        {
            if (currentStatus?.BeaconLight == (on ? 0 : 1))
            {
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.BEACON_TOGGLE, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void LandingSet(bool on)
        {
            if (currentStatus?.LandingLight == (on ? 0 : 1))
            {
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.LANDING_LIGHTS_TOGGLE, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void TaxiSet(bool on)
        {
            if (currentStatus?.TaxiLight == (on ? 0 : 1))
            {
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.TAXI_TOGGLE, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void NavSet(bool on)
        {
            if (currentStatus?.NavLight == (on ? 0 : 1))
            {
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.NAV_TOGGLE, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void StrobeSet(bool on)
        {
            if (currentStatus?.StrobeLight == (on ? 0 : 1))
            {
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, on ? EVENTS.STROBE_ON : EVENTS.STROBE_OFF, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void MasterAltSet(bool on)
        {
            if (currentStatus?.Engine1Generator == (on ? 0 : 1))
            {
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.MASTER_ALT_TOGGLE, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void BatterySet(bool on)
        {
            //if (currentStatus?.MasterBattery == (on ? 0 : 1))
            {
                simconnect?.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.MASTER_BATTERY_TOGGLE, 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        public void AvionicsSet(bool on)
        {
            //if (currentStatus?.MasterAvionics == (on ? 0 : 1))
            {
                simconnect?.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.MASTER_AVIONICS_SET, on ? 1u : 0, GROUPID.MAX, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
        }

        private void simconnect_OnRecvSimobjectDataBytypeAsync(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
        {
            // Must be general SimObject information
            switch (data.dwRequestID)
            {
                case (uint)DATA_REQUESTS.PLANE_STATUS:
                    {
                        var status = data.dwData[0] as PlaneStatusStruct?;

                        if (status.HasValue)
                        {
                            currentStatus = status;
                            PlaneStatusUpdated?.Invoke(this, new PlaneStatusUpdatedEventArgs(new PlaneStatus(status.Value)));
                        }
                        else
                        {
                            // Cast failed
                            logger.LogError("Cannot cast to FlightStatusStruct!");
                        }
                    }
                    break;
            }
        }

        void simconnect_OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            logger.LogInformation("OnRecvEvent dwID " + data.dwID + " uEventID " + data.uEventID);
            switch ((SIMCONNECT_RECV_ID)data.dwID)
            {
                case SIMCONNECT_RECV_ID.EVENT_FILENAME:

                    break;
                case SIMCONNECT_RECV_ID.QUIT:
                    logger.LogInformation("Quit");
                    break;
            }

            //switch ((EVENTS)data.uEventID)
            //{
            //    case EVENTS.SIM_START:
            //        logger.LogInformation("Sim start");
            //        break;
            //    case EVENTS.SIM_STOP:
            //        logger.LogInformation("Sim stop");
            //        break;
            //    case EVENTS.PAUSED:
            //        logger.LogInformation("Paused");
            //        //simconnect.TransmitClientEvent((uint)SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.SEND_UNPAUSE, (uint)0, GROUPID.FLAG, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            //        break;
            //}
        }

        void simconnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            logger.LogInformation("Connected to Flight Simulator");

            cts?.Cancel();
            cts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(StatusDelayMilliseconds);
                        cts?.Token.ThrowIfCancellationRequested();
                        simconnect?.RequestDataOnSimObjectType(DATA_REQUESTS.PLANE_STATUS, DEFINITIONS.PlaneStatus, 0, SIMCONNECT_SIMOBJECT_TYPE.USER);
                    }
                }
                catch (TaskCanceledException) { }
            });
        }

        // The case where the user closes Prepar3D
        void simconnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            CloseConnection();
        }

        void simconnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            logger.LogError("Exception received: {0}", data.dwException);
        }

        private void RecoverFromError()
        {
            string errorMessage;
            //Disconnect();

            //bool wasSuccess = Connect(out errorMessage);

            //// Start monitoring the user's SimObject. This will continuously monitor information
            //// about the user's Stations attached to their SimObject.
            //if (wasSuccess)
            //{
            //    StartMonitoring();
            //}
        }
    }
}
