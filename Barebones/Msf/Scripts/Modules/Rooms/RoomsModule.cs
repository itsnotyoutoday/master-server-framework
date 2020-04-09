﻿using Barebones.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Barebones.MasterServer
{
    public class RoomsModule : BaseServerModule, IGamesProvider
    {
        #region Unity Inspector

        [Header("Permissions")]
        [Tooltip("Minimal permission level, necessary to register a room")]
        [SerializeField]
        protected int registerRoomPermissionLevel = 0;

        #endregion

        /// <summary>
        /// Next room id
        /// </summary>
        private int nextRoomId = 0;

        /// <summary>
        /// Registered rooms list
        /// </summary>
        protected Dictionary<int, RegisteredRoom> roomsList;

        /// <summary>
        /// Fired when new room is registered
        /// </summary>
        public event Action<RegisteredRoom> OnRoomRegisteredEvent;

        /// <summary>
        /// Fired when existing room is destroyed
        /// </summary>
        public event Action<RegisteredRoom> OnRoomDestroyedEvent;

        protected override void Awake()
        {
            base.Awake();

            roomsList = new Dictionary<int, RegisteredRoom>();
        }

        public override void Initialize(IServer server)
        {
            // Add handlers
            server.SetHandler((short)MsfMessageCodes.RegisterRoomRequest, RegisterRoomRequestHandler);
            server.SetHandler((short)MsfMessageCodes.DestroyRoomRequest, DestroyRoomRequestHandler);
            server.SetHandler((short)MsfMessageCodes.SaveRoomOptionsRequest, SaveRoomOptionsRequestHandler);
            server.SetHandler((short)MsfMessageCodes.GetRoomAccessRequest, GetRoomAccessRequestHandler);
            server.SetHandler((short)MsfMessageCodes.ValidateRoomAccessRequest, ValidateRoomAccessRequestHandler);
            server.SetHandler((short)MsfMessageCodes.PlayerLeftRoomRequest, PlayerLeftRoomRequestHandler);

            // Maintain unconfirmed accesses
            InvokeRepeating(nameof(CleanUnconfirmedAccesses), 1f, 1f);
        }

        /// <summary>
        /// Cleans up the list of unconfirmed accesses
        /// </summary>
        private void CleanUnconfirmedAccesses()
        {
            foreach (var registeredRoom in roomsList.Values)
            {
                registeredRoom.ClearTimedOutAccesses();
            }
        }

        /// <summary>
        /// Returns true, if peer has permissions to register a game server
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        protected virtual bool HasRoomRegistrationPermissions(IPeer peer)
        {
            var extension = peer.GetExtension<SecurityInfoPeerExtension>();
            return extension.PermissionLevel >= registerRoomPermissionLevel;
        }

        /// <summary>
        /// Fired when registered room peer disconnected from master
        /// </summary>
        /// <param name="peer"></param>
        private void OnRegisteredPeerDisconnect(IPeer peer)
        {
            Dictionary<int, RegisteredRoom> peerRooms = peer.GetProperty((int)MsfPropCodes.RegisteredRooms) as Dictionary<int, RegisteredRoom>;

            if (peerRooms == null)
            {
                return;
            }

            logger.Debug($"Client {peer.Id} was disconnected from server and it has registered rooms that also must be destroyed");

            // Create a copy so that we can iterate safely
            var registeredRooms = peerRooms.Values.ToList();

            foreach (var registeredRoom in registeredRooms)
            {
                DestroyRoom(registeredRoom);
            }
        }

        /// <summary>
        /// Get next room id
        /// </summary>
        /// <returns></returns>
        public int GenerateRoomId()
        {
            return nextRoomId++;
        }

        /// <summary>
        /// Registers a room to the server
        /// </summary>
        /// <param name="peer"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public virtual RegisteredRoom RegisterRoom(IPeer peer, RoomOptions options)
        {
            // Create the object
            var room = new RegisteredRoom(GenerateRoomId(), peer, options);
            var peerRooms = peer.GetProperty((int)MsfPropCodes.RegisteredRooms) as Dictionary<int, RegisteredRoom>;

            if (peerRooms == null)
            {
                // If this is the first time creating a room

                // Save the dictionary
                peerRooms = new Dictionary<int, RegisteredRoom>();
                peer.SetProperty((int)MsfPropCodes.RegisteredRooms, peerRooms);

                // Listen to disconnect event
                peer.OnPeerDisconnectedEvent += OnRegisteredPeerDisconnect;
            }

            // Add a new room to peer
            peerRooms[room.RoomId] = room;

            // Add the room to a list of all rooms
            roomsList[room.RoomId] = room;

            // Invoke the event
            OnRoomRegisteredEvent?.Invoke(room);

            return room;
        }

        /// <summary>
        /// Unregisters a room from a server
        /// </summary>
        /// <param name="room"></param>
        public virtual void DestroyRoom(RegisteredRoom room)
        {
            var peer = room.Peer;

            if (peer != null)
            {
                var peerRooms = peer.GetProperty((int)MsfPropCodes.RegisteredRooms) as Dictionary<int, RegisteredRoom>;

                // Remove the room from peer
                if (peerRooms != null)
                {
                    peerRooms.Remove(room.RoomId);
                }
            }

            // Remove the room from all rooms
            roomsList.Remove(room.RoomId);
            room.Destroy();

            logger.Debug($"Room {room.RoomId} has been successfully destroyed");

            // Invoke the event
            OnRoomDestroyedEvent?.Invoke(room);
        }

        /// <summary>
        /// There are times when you need to change registered room options. This method will help you :)
        /// </summary>
        /// <param name="room"></param>
        /// <param name="options"></param>
        public virtual void ChangeRoomOptions(RegisteredRoom room, RoomOptions options)
        {
            room.ChangeOptions(options);
        }

        /// <summary>
        /// Returns list of all public rooms by given filter
        /// </summary>
        /// <param name="peer"></param>
        /// <param name="filters"></param>
        /// <returns></returns>
        public IEnumerable<GameInfoPacket> GetPublicGames(IPeer peer, Dictionary<string, string> filters)
        {
            return roomsList.Values.Where(r => r.Options.IsPublic).Select(r => new GameInfoPacket()
            {
                Id = r.RoomId,
                Address = r.Options.RoomIp + ":" + r.Options.RoomPort,
                MaxPlayers = r.Options.MaxConnections,
                Name = r.Options.Name,
                OnlinePlayers = r.OnlineCount,
                Properties = GetPublicRoomProperties(peer, r, filters),
                IsPasswordProtected = !string.IsNullOrEmpty(r.Options.Password),
                Type = GameInfoType.Room,
                Region = "SuperPuper"
            });
        }

        /// <summary>
        /// Returns list of properties of public room
        /// </summary>
        /// <param name="player"></param>
        /// <param name="room"></param>
        /// <param name="playerFilters"></param>
        /// <returns></returns>
        public virtual Dictionary<string, string> GetPublicRoomProperties(IPeer player, RegisteredRoom room, Dictionary<string, string> playerFilters)
        {
            return room.Options.Properties;
        }

        /// <summary>
        /// Returns room by given id
        /// </summary>
        /// <param name="roomId"></param>
        /// <returns></returns>
        public RegisteredRoom GetRoom(int roomId)
        {
            return roomsList[roomId];
        }

        /// <summary>
        /// Returns the list of all registered rooms
        /// </summary>
        /// <returns></returns>
        public IEnumerable<RegisteredRoom> GetAllRooms()
        {
            return roomsList.Values;
        }

        #region Message Handlers

        private void RegisterRoomRequestHandler(IIncommingMessage message)
        {
            logger.Debug($"Client {message.Peer.Id} requested to register new room server");

            if (!HasRoomRegistrationPermissions(message.Peer))
            {
                logger.Debug($"But it has no permission");
                message.Respond("Insufficient permissions", ResponseStatus.Unauthorized);
                return;
            }

            var options = message.Deserialize(new RoomOptions());
            var room = RegisterRoom(message.Peer, options);

            logger.Debug($"Room {room.RoomId} has been successfully registered with options: {options}");

            // Respond with a room id
            message.Respond(room.RoomId, ResponseStatus.Success);
        }

        protected virtual void DestroyRoomRequestHandler(IIncommingMessage message)
        {
            var roomId = message.AsInt();

            logger.Debug($"Client {message.Peer.Id} requested to destroy room server with id {roomId}");

            if (!roomsList.TryGetValue(roomId, out RegisteredRoom room))
            {
                logger.Debug($"But this room does not exist");
                message.Respond("Room does not exist", ResponseStatus.Failed);
                return;
            }

            if (message.Peer != room.Peer)
            {
                logger.Debug($"But it is not the creator of the room");
                message.Respond("You're not the creator of the room", ResponseStatus.Unauthorized);
                return;
            }

            DestroyRoom(room);

            logger.Debug($"Room {roomId} has been successfully destroyed");

            message.Respond(ResponseStatus.Success);
        }

        private void ValidateRoomAccessRequestHandler(IIncommingMessage message)
        {
            var data = message.Deserialize(new RoomAccessValidatePacket());

            if (!roomsList.TryGetValue(data.RoomId, out RegisteredRoom room))
            {
                message.Respond("Room does not exist", ResponseStatus.Failed);
                return;
            }

            if (message.Peer != room.Peer)
            {
                // Wrong peer unregistering the room
                message.Respond("You're not the creator of the room", ResponseStatus.Unauthorized);
                return;
            }

            if (!room.ValidateAccess(data.Token, out IPeer playerPeer))
            {
                message.Respond("Failed to confirm the access", ResponseStatus.Unauthorized);
                return;
            }

            var packet = new UsernameAndPeerIdPacket()
            {
                PeerId = playerPeer.Id
            };

            // Add username if available
            var userExt = playerPeer.GetExtension<IUserPeerExtension>();
            if (userExt != null)
            {
                packet.Username = userExt.Username ?? "";
            }

            // Respond with success and player's peer id
            message.Respond(packet, ResponseStatus.Success);
        }

        protected virtual void SaveRoomOptionsRequestHandler(IIncommingMessage message)
        {
            var data = message.Deserialize(new SaveRoomOptionsPacket());

            if (!roomsList.TryGetValue(data.RoomId, out RegisteredRoom room))
            {
                message.Respond("Room does not exist", ResponseStatus.Failed);
                return;
            }

            if (message.Peer != room.Peer)
            {
                // Wrong peer unregistering the room
                message.Respond("You're not the creator of the room", ResponseStatus.Unauthorized);
                return;
            }

            ChangeRoomOptions(room, data.Options);

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void GetRoomAccessRequestHandler(IIncommingMessage message)
        {
            var data = message.Deserialize(new RoomAccessRequestPacket());

            if (!roomsList.TryGetValue(data.RoomId, out RegisteredRoom room))
            {
                message.Respond("Room does not exist", ResponseStatus.Failed);
                return;
            }

            if (!string.IsNullOrEmpty(room.Options.Password) && room.Options.Password != data.Password)
            {
                message.Respond("Invalid password", ResponseStatus.Unauthorized);
                return;
            }

            // Send room access request to peer who owns it
            room.GetAccess(message.Peer, data.Properties, (packet, error) =>
            {
                if (packet == null)
                {
                    message.Respond(error, ResponseStatus.Unauthorized);
                    return;
                }

                message.Respond(packet, ResponseStatus.Success);
            });
        }

        private void PlayerLeftRoomRequestHandler(IIncommingMessage message)
        {
            var data = message.Deserialize(new PlayerLeftRoomPacket());

            if (!roomsList.TryGetValue(data.RoomId, out RegisteredRoom room))
            {
                message.Respond("Room does not exist", ResponseStatus.Failed);
                return;
            }

            if (message.Peer != room.Peer)
            {
                // Wrong peer unregistering the room
                message.Respond("You're not the creator of the room", ResponseStatus.Unauthorized);
                return;
            }

            room.OnPlayerLeft(data.PeerId);

            message.Respond(ResponseStatus.Success);
        }

        #endregion
    }
}


