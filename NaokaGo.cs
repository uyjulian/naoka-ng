﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Photon.Hive.Plugin;

namespace NaokaGo
{
    /// <summary>
    ///     Static configuration "global" for cleaner access.
    /// </summary>
    public class NaokaConfig
    {
        public IPluginLogger Logger;
        public IPluginHost Host;
        public Dictionary<string, string> Config;
        public Dictionary<string, string> ApiConfig;
        public Dictionary<string, object> RuntimeConfig; // Runtime configuration, including rate-limits, max actors per ip, etc.

        public Dictionary<int, Dictionary<string, object>> ActorsInternalProps = new Dictionary<int, Dictionary<string, object>>();
    }

    public class NaokaGo : PluginBase
    {
        private NaokaConfig naokaConfig = new NaokaConfig();
        private EventLogic _EventLogic = new EventLogic();

        public override string Name => "Naoka";

        public override bool SetupInstance(IPluginHost host, Dictionary<string, string> config, out string errorMsg)
        {
            RegisterCustomTypes();
            _EventLogic.naokaConfig = naokaConfig;
            naokaConfig.Logger = host.CreateLogger(Name);
            naokaConfig.Host = host;
            naokaConfig.Config = config;
            naokaConfig.ApiConfig = new Dictionary<string, string>
            {
                {"ApiUrl", config["ApiUrl"]},
                {"PhotonSecret", config["PhotonApiSecret"]}
            };
            naokaConfig.RuntimeConfig = new Dictionary<string, object>()
            {
                {"maxAccPerIp", 5},
            };

            // Task.Run(_EventLogic.RunEvent8Timer);
            
            return base.SetupInstance(host, config, out errorMsg);
        }

        public override void OnCreateGame(ICreateGameCallInfo info)
        {
            naokaConfig.RuntimeConfig["gameId"] = info.Request.GameId;
            
            PhotonValidateJoinJWTResponse jwtValidationResult =
                _EventLogic.ValidateJoinJWT((string) ((Hashtable) info.Request.Parameters[248])[(byte) 2]);
            bool tokenValid = jwtValidationResult.Valid;

            if (!tokenValid)
            {
                info.Fail("Invalid JWT presented.");
                return;
            }

            string userId = jwtValidationResult.User.Id;
            string ipAddress = jwtValidationResult.Ip;

            naokaConfig.ActorsInternalProps.Add(1, new Dictionary<string, object>()
            {
                {"actorNr", 1}, {"userId", userId}, {"ip", ipAddress},
                {"jwtProperties", jwtValidationResult}, {"instantiated", false},
                {"hasOverriddenUserProps", false}, {"overriddenUserProps", new Dictionary<string, object>(){}}
            });
            
            _EventLogic.PrepareProperties(1, info.Request.ActorProperties, out var newProperties, out var error);
            if (error != "")
            {
                info.Fail(error);
            }

            info.Request.ActorProperties = newProperties;
            info.Continue();
            _EventLogic.SendModerationRequestToActor(1);
        }

        public override void OnJoin(IJoinGameCallInfo info)
        {
            PhotonValidateJoinJWTResponse jwtValidationResult = _EventLogic.ValidateJoinJWT((string)((Hashtable)info.Request.Parameters[248])[(byte)2]);
            bool tokenValid = jwtValidationResult.Valid;

            if (!tokenValid)
            {
                info.Fail("Invalid JWT presented.");
                return;
            }

            string userId = jwtValidationResult.User.Id;
            string ipAddress = jwtValidationResult.Ip;

            int ipAddressCount = 0;
            foreach (var actorInternalProp in naokaConfig.ActorsInternalProps)
            {
                if ((string)actorInternalProp.Value["ip"] == ipAddress)
                    ++ipAddressCount;

                if (ipAddressCount > (int) naokaConfig.RuntimeConfig["maxAccPerIp"])
                {
                    info.Fail("Max. account limit per instance reached. You've been bapped.");
                    return;
                }
            }

            naokaConfig.ActorsInternalProps.Add(info.ActorNr, new Dictionary<string, object>() 
            {
                {"actorNr", info.ActorNr},
                {"userId", userId}, {"ip", ipAddress}, {"jwtProperties", jwtValidationResult}, {"instantiated", false},
                {"hasOverriddenUserProps", false}, {"overriddenUserProps", new Dictionary<string, object>(){}}
            });
            
            _EventLogic.PrepareProperties(info.ActorNr, info.Request.ActorProperties, out var newProperties, out var error);
            if (error != "")
            {
                info.Fail(error);
            }

            info.Request.ActorProperties = newProperties;
            info.Continue();
            
            _EventLogic.SendModerationRequestToActor(info.ActorNr);
            _EventLogic.SendModerationRequestToAllForActor(info.ActorNr);
        }

        public override void OnCloseGame(ICloseGameCallInfo info)
        {
            // TODO: Implement API call for instance removal. (e.g.: Reduce player count for instance in API).
            info.Continue();
        }

        public override void BeforeSetProperties(IBeforeSetPropertiesCallInfo info)
        {
            if (info.Request.ActorNumber == 0 || info.Request.ActorNumber != info.ActorNr)
            {
                naokaConfig.Host.RemoveActor(info.ActorNr, "*TeamSpeak Voice*: You have been kicked from the server.");
                info.Fail("*TeamSpeak Voice*: You have been kicked from the server.");
                return;
            }
            
            info.Request.Broadcast = true;

            _EventLogic.PrepareProperties(info.ActorNr, info.Request.Properties, out var temporaryPropertiesHT, out var propertiesError);
            if (propertiesError != "")
            {
                info.Fail(propertiesError);
                return;
            }
            
            info.Request.Properties = temporaryPropertiesHT;
            info.Request.Parameters[251] = temporaryPropertiesHT; // Better be safe than sorry.
            
            info.Continue();
        }

        public override void OnLeave(ILeaveGameCallInfo info)
        {
            naokaConfig.ActorsInternalProps.Remove(info.ActorNr);
            info.Continue();
        }
        
        public override void OnSetProperties(ISetPropertiesCallInfo info)
        {
            info.Continue();
        }

        public override void OnRaiseEvent(IRaiseEventCallInfo info)
        {
            switch (info.Request.EvCode)
            {
                case 0: // Unused event code. Trigger an alert if someone attempts to use this.
                    naokaConfig.Logger.Warn($"[Naoka]: EvCode 0 (Unused) called by {info.ActorNr} ({naokaConfig.ActorsInternalProps[info.ActorNr]["userId"]}), This is very suspicious.");
                    info.Fail("Unauthorized.");
                    return;

                case 1: // VoiceDataReceived | uSpeak
                    info.Continue();
                    break;

                case 2: // ExecutiveMessage
                    naokaConfig.Logger.Warn($"[Naoka]: EvCode 2 (ExecutiveMessage) called by {info.ActorNr} ({naokaConfig.ActorsInternalProps[info.ActorNr]["userId"]}), This is very suspicious.");
                    info.Fail("Unauthorized.");
                    return;

                case 3: // SendPastEvents; Only sent to MasterClient upon join.
                    // TODO: Force it to be only sent to MasterClient.
                    // int[] masterOnly = { naokaConfig.Host.MasterClientId };
                    // if (info.Request.Actors != masterOnly)
                    // {
                        // naokaConfig.Logger.Info("[Naoka]: Event 3 was not sent only to MasterClient");
                        // info.Fail("Event 3 was not sent only to MasterClient.");
                        //return;
                    // }

                    info.Continue();
                    break;

                case 4: // SyncEvents
                case 5: // InitialSyncFinished
                case 6: // ProcessEvent
                case 7: // Serialization
                    // Events 4, 5, 6, and 7 are directly passed through without further processing.
                    info.Continue();
                    break;

                case 8: // ReceiveInterval (Interest List)
                    // TODO: Implement interest list sync.
                    // TODO: Channel rollover ->
                    //       Initial = 100,
                    //       RolloverPoint = 201 (exclusive)
                    //       On every evt8, increment by 1, roll back to 100 when 200 is reached and responded to.

                    // var eventData8 = (byte[]) info.Request.Parameters[245];
                    // logEventEight(info.ActorNr, eventData8);

                    var parameters = new Dictionary<byte, object>();
                    int[] playerViews = naokaConfig.Host.GameActors.Select(actor => actor.ActorNr).ToArray();
                    parameters.Add(245, playerViews);
                    parameters.Add(254, 0);
                    SendParameters sendParams = default;
                    naokaConfig.Host.BroadcastEvent(
                        new List<int> {info.ActorNr},
                        0,
                        8,
                        parameters,
                        0, sendParams
                    );

                    info.Cancel();
                    break;

                case 9: // Udon, AV3Sync, BigData/ChairSync.
                case 15: // BigData/ChairSync? (Some sources say it's this, while others say it's 9).
                    info.Continue();
                    break;

                case 33: // ExecutiveAction
                    Dictionary<byte,object> eventData = (Dictionary<byte,object>)info.Request.Parameters[245];

                    switch((byte) eventData[ExecutiveActionPacket.Type])
                    {
                        case ExecutiveActionTypes.Request_PlayerMods:
                        {
                            // This is a reply to the server's request.
                            // It contains a String[][]:
                            //   - []string: blockedUsers
                            //   - []string: mutedUsers
                            // The strings are the first six characters of the user's ID, exclusive of `usr_`

                            var blockedUsers = ((string[][]) eventData[3])[0];
                            var mutedUsers = ((string[][]) eventData[3])[1];

                            foreach (var actor in naokaConfig.ActorsInternalProps)
                            {
                                if ((int) actor.Value["actorNr"] != info.ActorNr)
                                {
                                    var u = ((string) actor.Value["userId"]).Substring(4, 6);
                                    var isBlocked = blockedUsers.Contains(u);
                                    var isMuted = mutedUsers.Contains(u);

                                    _EventLogic.SendModerationReply(info.ActorNr, actor.Key, isBlocked,
                                        isMuted);
                                }
                            }

                            break;
                        }
                    }

                    info.Cancel();
                    break;

                case 40: // UserRecordUpdate
                    _EventLogic.PullPartialActorProperties(info.ActorNr, (string)naokaConfig.ActorsInternalProps[info.ActorNr]["userId"]);

                    info.Cancel();
                    break;

                default: // Unknown event code; Log and cancel.
                    if (info.Request.EvCode < 200)
                    {
                        naokaConfig.Logger.Warn($"[Naoka]: Unknown Event code `{info.Request.EvCode}`.");
                        info.Cancel();
                    }
                    break;
            }

            if (info.Request.EvCode >= 200)
            {
                switch (info.Request.EvCode)
                {
                    case 202: // TODO: Force instantiation at 0,0,0. | Custom Types.
                        if (((Hashtable)info.Request.Parameters[245]).Contains((byte)0) &&
                            (string)((Hashtable)info.Request.Parameters[245])[0] == "VRCPlayer" && 
                            (bool)naokaConfig.ActorsInternalProps[info.ActorNr]["instantiated"])
                        {
                            info.Fail("Already instatiated");
                            return;
                        }

                        if ((string) ((Hashtable) info.Request.Parameters[245])[0] == "VRCPlayer")
                            naokaConfig.ActorsInternalProps[info.ActorNr]["instantiated"] = true;
                        info.Request.Cache = CacheOperations.AddToRoomCache;

                        break;
                }
                info.Continue();
            }
        }

        private void RegisterCustomTypes()
        {
        }

        private void logEventEight(int actorNr, byte[] data)
        {
            var actorsInEvent8 = data.Length / 6;
            var posInEvent8 = 0;

            var actorInterests = new Dictionary<Int32, Int16>() { };
            while (posInEvent8 < data.Length)
            {
                var viewid = BitConverter.ToInt32(data, posInEvent8);
                posInEvent8 += 4;
                var interest = BitConverter.ToInt16(data, posInEvent8);
                actorInterests.Add(viewid, interest);
                posInEvent8 += 2;
            }

            foreach (var kvp in actorInterests)
            {
                naokaConfig.Logger.Info($"[Naoka]:    {actorNr} set interest of ViewID {kvp.Key} to {kvp.Value}");
            }
        }
    }

    public class NaokaGoFactory : PluginFactoryBase
    {
        public override IGamePlugin CreatePlugin(string pluginName)
        {
            return new NaokaGo();
        }
    }
}