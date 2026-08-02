using System;
using SotnApi.Interfaces;
using SotnRandoTools.Configuration.Interfaces;
using SotnRandoTools.Coop.Enums;
using SotnRandoTools.Coop.Interfaces;
using SotnRandoTools.Services;

namespace SotnRandoTools.Coop
{
	internal sealed class CoopSender
	{
		private readonly IToolConfig toolConfig;
		private readonly ISotnApi sotnApi;
		private readonly INotificationService notificationService;
		private readonly ICoopController coopController;
		private bool sendPressedFrame1 = false;
		private bool sendPressedFrame2 = false;
		private bool sendPressed = false;
		private bool inGame = false;
		private bool gameStarted = false;
		byte[] data2 = new byte[2];
		byte[] data3 = new byte[3];
		byte[] data5 = new byte[5];
		byte[] data9 = new byte[9];
		// New allocated byte array for coordinate tracking: Type(1), PlayerId(1), X(2), Y(2)
		byte[] data6 = new byte[6];

		private int mapFrameCounter = 0;

		// Network throttle tracking variables
		private DateTime lastPositionSendTime = DateTime.MinValue;
		private const double PositionSendIntervalMs = 100; // Broadcast 10 times per second
		private ushort lastLoggedX = 0;
		private ushort lastLoggedY = 0;
		private ushort lastLoggedRoom = 0;
		private ushort[] sendButton = new ushort[4] { SotnApi.Constants.Values.Game.Controller.Select, SotnApi.Constants.Values.Game.Controller.Triangle, SotnApi.Constants.Values.Game.Controller.L3, SotnApi.Constants.Values.Game.Controller.R3 };

		public CoopSender(IToolConfig toolConfig, ISotnApi sotnApi, INotificationService notificationService, ICoopController coopController)
		{
			this.toolConfig = toolConfig ?? throw new ArgumentNullException(nameof(toolConfig));
			this.sotnApi = sotnApi ?? throw new ArgumentNullException(nameof(sotnApi)); ;
			this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
			this.coopController = coopController ?? throw new ArgumentNullException(nameof(coopController));
		}

		public void Update()
		{
			if (!gameStarted && sotnApi.GameApi.InAlucardMode())
			{
				gameStarted = true;
				inGame = true;
			}
			if (gameStarted && sotnApi.GameApi.Status == SotnApi.Constants.Values.Game.Status.MainMenu)
			{
				inGame = false;
				return;
			}
			if (gameStarted && !inGame && sotnApi.GameApi.InAlucardMode() && coopController.IsConnected())
			{
				inGame = true;
				SendSynchRequest();
			}
			if (!sotnApi.GameApi.InAlucardMode() || !coopController.IsConnected())
			{
				return;
			}
			CheckSendButton();
			SendRelics();
			SendItem();
			SendLocations();
			SendWarps();
			SendShortcuts();
			SendLocalMapTelemetry();

			if (coopController.SynchRequested)
			{
				coopController.SynchRequested = false;
				SendSynchAll();
			}

			CheckSynchRequest();
		}

		private void CheckSendButton()
		{
			sendPressedFrame1 = sendPressedFrame2;
			if ((sotnApi.GameApi.InputFlags & sendButton[toolConfig.Coop.SendButton]) == sendButton[toolConfig.Coop.SendButton])
			{
				sendPressedFrame2 = true;
			}
			else
			{
				sendPressedFrame2 = false;
			}

			if (sendPressedFrame2 && !sendPressedFrame1)
			{
				sendPressed = true;
			}
			else
			{
				sendPressed = false;
			}
		}
		private unsafe void SendLocalMapTelemetry()
		{
			// 1. Map History Synchronization: Runs exactly once every 10 frames
			mapFrameCounter++;
			if (mapFrameCounter >= 10)
			{
				mapFrameCounter = 0;

				ushort currentMapX = (ushort) sotnApi.AlucardApi.MapX;
				ushort currentMapY = (ushort) sotnApi.AlucardApi.MapY;

				if (currentMapX != lastLoggedRoom || currentMapY != lastLoggedY)
				{
					lastLoggedRoom = currentMapX;

					// FIX: Read whether the local player is in the Second Castle right now
					byte activeCastle = (byte) (sotnApi.GameApi.SecondCastle ? 2 : 1);

					fixed (byte* buffer = data6)
					{
						buffer[0] = (byte) MessageType.RoomHistory;
						buffer[1] = activeCastle; // ◄ Store the specific castle state in byte 1

						Array.Copy(BitConverter.GetBytes(currentMapX), 0, data6, 2, 2);
						Array.Copy(BitConverter.GetBytes(currentMapY), 0, data6, 4, 2);
					}
					coopController.SendData(data6);
				}
			}

			// 2. Continuous Real-time Live Location Blinking Circle Node Dot Tracker Streaming (100ms interval)
			if ((DateTime.UtcNow - lastPositionSendTime).TotalMilliseconds >= PositionSendIntervalMs)
			{
				lastPositionSendTime = DateTime.UtcNow;

				ushort currentX = (ushort) sotnApi.AlucardApi.MapX;
				ushort currentY = (ushort) sotnApi.AlucardApi.MapY;

				if (currentX != lastLoggedX || currentY != lastLoggedY)
				{
					lastLoggedX = currentX;
					lastLoggedY = currentY;

					fixed (byte* buffer = data6)
					{
						buffer[0] = (byte) MessageType.PlayerCoords;
						buffer[1] = 0;
						*((ushort*) (buffer + 2)) = currentX;
						*((ushort*) (buffer + 4)) = currentY;
					}
					coopController.SendData(data6);
				}
			}
		}
		private unsafe void SendItem()
		{
			if (!sotnApi.GameApi.EquipMenuOpen() || !sotnApi.GameApi.IsInMenu() || !sendPressed)
			{
				return;
			}

			sendPressed = true;
			short item = (short) sotnApi.AlucardApi.GetSelectedItem();
			if (item == -1 || !sotnApi.AlucardApi.HasItemInInventory(item))
			{
				return;
			}
			sotnApi.AlucardApi.TakeOneItem(item);
			fixed (byte* buffer = data3)
			{
				buffer[0] = (byte) MessageType.Item;
				*((short*) (buffer + 1)) = item;
			}
			coopController.SendData(data3);
		}

		private void SendRelics()
		{
			for (int i = 0; i < coopController.CoopState.relics.Length; i++)
			{
				if (coopController.CoopState.relics[i].updated && coopController.CoopState.relics[i].status)
				{
					data2[0] = (byte) MessageType.Relic;
					data2[1] = (byte) i;
					coopController.SendData(data2);
				}
			}
		}

		private unsafe void SendLocations()
		{
			for (ushort i = 0; i < coopController.CoopState.locations.Length; i++)
			{
				if (coopController.CoopState.locations[i].updated && coopController.CoopState.locations[i].status)
				{
					ushort roomIndex = coopController.CoopState.locations[i].roomIndex;
					ushort locationIndex = i;
					fixed (byte* buffer = data5)
					{
						buffer[0] = (byte) MessageType.Location;
						*((ushort*) (buffer + 1)) = roomIndex;
						*((ushort*) (buffer + 3)) = locationIndex;
					}
					coopController.SendData(data5);
				}
			}
		}

		private void SendWarps()
		{
			if (coopController.CoopState.WarpsFirstCastle.updated)
			{
				data2[0] = (byte) MessageType.WarpFirstCastle;
				data2[1] = coopController.CoopState.WarpsFirstCastle.difference;
				coopController.SendData(data2);
				//Console.WriteLine($"Sending first castle warp {coopController.CoopState.WarpsFirstCastle.difference}.");
			}
			if (coopController.CoopState.WarpsSecondCastle.updated)
			{
				data2[0] = (byte) MessageType.WarpSecondCastle;
				data2[1] = coopController.CoopState.WarpsSecondCastle.difference;
				coopController.SendData(data2);
				//Console.WriteLine($"Sending first castle warp {coopController.CoopState.WarpsSecondCastle.difference}.");
			}
		}

		private void SendShortcuts()
		{
			for (int i = 0; i < coopController.CoopState.shortcuts.Length; i++)
			{
				if (coopController.CoopState.shortcuts[i].updated && coopController.CoopState.shortcuts[i].status)
				{
					data2[0] = (byte) MessageType.Shortcut;
					data2[1] = (byte) i;
					coopController.SendData(data2);
				}
			}
		}

		private void CheckSynchRequest()
		{
			if (!sotnApi.GameApi.RelicMenuOpen() || !sotnApi.GameApi.IsInMenu() || !sendPressed)
			{
				return;
			}
			notificationService.AddMessage("Requested Synch");
			SendSynchRequest();
		}

		private void SendSynchRequest()
		{
			data2[0] = (byte) MessageType.SynchRequest;
			coopController.SendData(data2);
			Console.WriteLine("Requested synch");
		}

		private unsafe void SendSynchAll()
		{
			data9[0] = (byte) MessageType.SynchAll;
			data9[1] = coopController.CoopState.WarpsFirstCastle.value;
			data9[2] = coopController.CoopState.WarpsSecondCastle.value;
			ushort shortcuts = 0;
			for (ushort i = 0; i < coopController.CoopState.shortcuts.Length; i++)
			{
				if (coopController.CoopState.shortcuts[i].status)
				{
					shortcuts |= (ushort) Math.Pow(2, i);
				}
			}
			int relicsNumber = 0;
			for (int i = 0; i < coopController.CoopState.relics.Length; i++)
			{
				if (coopController.CoopState.relics[i].status)
				{
					relicsNumber |= (int) Math.Pow(2, i);
				}
			}
			fixed (byte* buffer = data9)
			{
				*((ushort*) (buffer + 3)) = shortcuts;
				*((int*) (buffer + 5)) = relicsNumber;
			}

			coopController.SendData(data9);
		}
	}
}
