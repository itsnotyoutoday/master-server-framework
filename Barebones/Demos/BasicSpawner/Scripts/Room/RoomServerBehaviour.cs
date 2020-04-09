﻿using Barebones.Networking;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Barebones.MasterServer.Examples.BasicSpawner
{
    public class RoomServerBehaviour : ServerBehaviour
    {
        /// <summary>
        /// This socket connects room server to master as client
        /// </summary>
        private IClientSocket msfConnection;

        /// <summary>
        /// Just an info about current connection process
        /// </summary>
        private bool isConnecting = false;

        /// <summary>
        /// Options of this room we must share with clients
        /// </summary>
        private RoomOptions roomOptions;

        [Header("Room Settings"), SerializeField]
        private HelpBox roomServerInfo = new HelpBox()
        {
            Text = "Room server component is responsable for connection to master server and starting room server",
            Type = HelpBoxType.Info
        };

        [SerializeField, Tooltip("If true, room can only run as spawned process. If you wish to run room server without spawner just check this box to false")]
        private bool startRoomAsProcess = true;

        [Header("Master Connection Settings")]
        [SerializeField]
        private string masterIp = "127.0.0.1";
        [SerializeField]
        private int masterPort = 5000;

        //[Header("Room Settings"), SerializeField]
        //private int maxRoomConnections = 4;

        /// <summary>
        /// Controller of the room
        /// </summary>
        public RoomController CurrentRoomController { get; private set; }

        /// <summary>
        /// Fires when server room is successfully registered
        /// </summary>
        public UnityEvent OnRoomServerRegisteredEvent;

        protected override void Awake()
        {
            base.Awake();

            // If master IP is provided via cmd arguments
            if (Msf.Args.IsProvided(Msf.Args.Names.MasterIp))
            {
                masterIp = Msf.Args.MasterIp;
            }

            // If master port is provided via cmd arguments
            if (Msf.Args.IsProvided(Msf.Args.Names.MasterPort))
            {
                masterPort = Msf.Args.MasterPort;
            }
        }

        protected override void Start()
        {
            // Start room server at start
            if ((Msf.Runtime.IsEditor && autoStartInEditor) || Msf.Args.AutoConnectClient)
            {
                // Start the server on next frame
                MsfTimer.WaitForEndOfFrame(() =>
                {
                    WaitPublicIpIfRequired(() =>
                    {
                        StartServer();
                    });
                });
            }
        }

        protected virtual void OnApplicationQuit()
        {
            if (msfConnection != null)
                msfConnection.Disconnect();
        }

        public override void StartServer()
        {
            // If you have started this scene in client mode
            if (Msf.Client.Rooms.ForceClientMode) return;

            // Read room options
            roomOptions = new RoomOptions
            {
                IsPublic = !Msf.Args.IsProvided(Msf.Args.Names.RoomIsPrivate),
                MaxConnections = Msf.Args.ExtractValueInt(Msf.Args.Names.RoomMaxConnections, 0),
                Name = Msf.Args.ExtractValue(Msf.Args.Names.RoomName, "Room_" + Msf.Helper.CreateRandomString(5)),
                Password = Msf.Args.ExtractValue(Msf.Args.Names.RoomPassword, string.Empty),
                RoomIp = Msf.Args.ExtractValue(Msf.Args.Names.RoomIp, serverIP),
                RoomPort = Msf.Args.ExtractValueInt(Msf.Args.Names.RoomPort, serverPort),
                Region = Msf.Args.ExtractValue(Msf.Args.Names.RoomRegion, "International")
            };

            logger.Info($"Starting Room Server... {Msf.Version}. Multithreading is: {(Msf.Runtime.SupportsThreads ? "On" : "Off")}");
            logger.Info($"Start parameters are: {Msf.Args}");

            // Create instance of the client socket
            msfConnection = Msf.Create.ClientSocket();

            // Override connections
            Msf.Server.Rooms.ChangeConnection(msfConnection);
            Msf.Server.Spawners.ChangeConnection(msfConnection);

            msfConnection.AddConnectionListener(OnConnectedToMasterHandler);

            StartConnectionToMaster();
        }

        private void StartConnectionToMaster()
        {
            if (msfConnection.IsConnected) return;

            // If connection process is started
            if (!isConnecting)
                logger.Info($"Connecting room server to master server at: {masterIp}:{masterPort}");
            else
                logger.Info($"Retrying connection of room server to master server at: {masterIp}:{masterPort}");

            // Set connection process checker as TRUE :)
            isConnecting = true;

            // Start client connection
            msfConnection.Connect(masterIp, masterPort);

            // Wait a result of client connection
            msfConnection.WaitForConnection((clientSocket) =>
            {
                if (!clientSocket.IsConnected)
                {
                    StartConnectionToMaster();
                }
                else
                {
                    logger.Info("Room server is successfuly connected to master server");
                }
            }, 2f);
        }

        /// <summary>
        /// Fired when this room server is connected to master as client
        /// </summary>
        protected virtual void OnConnectedToMasterHandler()
        {
            // Start the server on next frame
            MsfTimer.WaitForEndOfFrame(() =>
            {
                StartServer(roomOptions.RoomIp, roomOptions.RoomPort);
            });

            // Remove listener after successful connection
            msfConnection.RemoveConnectionListener(OnConnectedToMasterHandler);

            // Register disconnection listener
            msfConnection.AddDisconnectionListener(OnDisconnectedFromMasterHandler);

            // Before we register our room we need to setup everything
            BeforeRoomServerRegistering();
        }

        /// <summary>
        /// Fired when this room server is disconnected from master as client
        /// </summary>
        protected virtual void OnDisconnectedFromMasterHandler()
        {
            // Remove listener after disconnection
            msfConnection.RemoveDisconnectionListener(OnDisconnectedFromMasterHandler);

            // Quit the room. Master Server will restart the room
            Msf.Runtime.Quit();
        }

        /// <summary>
        /// Before we register our room we need to setup everything
        /// </summary>
        protected virtual void BeforeRoomServerRegistering()
        {
            if (startRoomAsProcess)
            {
                if (!Msf.Server.Spawners.IsSpawnedProccess)
                {
                    logger.Error("Room server process cannot be registered because it is not a spawned process");
                    return;
                }

                Msf.Server.Spawners.RegisterSpawnedProcess(Msf.Args.SpawnTaskId, Msf.Args.SpawnTaskUniqueCode, (taskController, error) =>
                {
                    if (taskController == null)
                    {
                        logger.Error($"Room server process cannot be registered. The reason is: {error}");
                        return;
                    }

                    // Then start registering our room server
                    RegisterRoomServer(() =>
                    {
                        logger.Info("Finalizing registration task");
                        taskController.FinalizeTask(new Dictionary<string, string>(), () =>
                        {
                            logger.Info("Ok!");
                            OnRoomServerRegisteredEvent?.Invoke();
                        });
                    });
                });
            }
            else
            {
                RegisterRoomServer(() =>
                {
                    logger.Info("Ok!");
                    OnRoomServerRegisteredEvent?.Invoke();
                });
            }
        }

        /// <summary>
        /// Start registering our room server
        /// </summary>
        protected virtual void RegisterRoomServer(UnityAction successCallback = null)
        {
            Msf.Server.Rooms.RegisterRoom(roomOptions, (controller, error) =>
            {
                if (controller == null)
                {
                    logger.Error(error);
                    return;
                }

                CurrentRoomController = controller;

                logger.Info($"Room Created successfully. Room ID: {controller.RoomId}, {roomOptions}");

                successCallback?.Invoke();
            });
        }
    }
}