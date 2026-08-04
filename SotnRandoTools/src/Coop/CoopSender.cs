using System;
using SotnApi.Interfaces;
using SotnApi.Constants.Addresses;
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
		byte[] data6 = new byte[6];

		private int mapFrameCounter = 0;

		private DateTime lastPositionSendTime = DateTime.MinValue;
		private const double PositionSendIntervalMs = 100;

		private ushort lastHistoryX = 0;
		private ushort lastHistoryY = 0;
		private ushort lastPositionX = 0;
		private ushort lastPositionY = 0;

		private ushort[] sendButton = new ushort[4]
		{
			SotnApi.Constants.Values.Game.Controller.Select,
			SotnApi.Constants.Values.Game.Controller.Triangle,
			SotnApi.Constants.Values.Game.Controller.L3,
			SotnApi.Constants.Values.Game.Controller.R3
		};

		public CoopSender(IToolConfig toolConfig, ISotnApi sotnApi, INotificationService notificationService, ICoopController coopController)
		{
			this.toolConfig = toolConfig ?? throw new ArgumentNullException(nameof(toolConfig));
			this.sotnApi = sotnApi ?? throw new ArgumentNullException(nameof(sotnApi));
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
			if (toolConfig.Coop.SendBossDefeat)
			{
				SendBosses();
			}

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
			mapFrameCounter++;
			if (mapFrameCounter >= 30)
			{
				mapFrameCounter = 0;

				ushort currentMapX = (ushort) sotnApi.AlucardApi.MapX;
				ushort currentMapY = (ushort) sotnApi.AlucardApi.MapY;

				if (currentMapX != lastHistoryX || currentMapY != lastHistoryY)
				{
					lastHistoryX = currentMapX;
					lastHistoryY = currentMapY;

					byte activeCastle = (byte) (sotnApi.GameApi.SecondCastle ? 2 : 1);

					fixed (byte* buffer = data6)
					{
						buffer[0] = (byte) MessageType.RoomHistory;
						buffer[1] = activeCastle;
						*((ushort*) (buffer + 2)) = currentMapX;
						*((ushort*) (buffer + 4)) = currentMapY;
					}
					coopController.SendData(data6);
				}
			}

			if ((DateTime.UtcNow - lastPositionSendTime).TotalMilliseconds >= PositionSendIntervalMs)
			{
				lastPositionSendTime = DateTime.UtcNow;

				ushort currentX = (ushort) sotnApi.AlucardApi.MapX;
				ushort currentY = (ushort) sotnApi.AlucardApi.MapY;

				if (currentX != lastPositionX || currentY != lastPositionY)
				{
					lastPositionX = currentX;
					lastPositionY = currentY;

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
					coopController.CoopState.relics[i].updated = false;
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
					coopController.CoopState.locations[i].updated = false;
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
				coopController.CoopState.WarpsFirstCastle.updated = false;
			}
			if (coopController.CoopState.WarpsSecondCastle.updated)
			{
				data2[0] = (byte) MessageType.WarpSecondCastle;
				data2[1] = coopController.CoopState.WarpsSecondCastle.difference;
				coopController.SendData(data2);
				coopController.CoopState.WarpsSecondCastle.updated = false;
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
					coopController.CoopState.shortcuts[i].updated = false;
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
			byte[] data13 = new byte[13];
			data13[0] = (byte) MessageType.SynchAll;
			data13[1] = coopController.CoopState.WarpsFirstCastle.value;
			data13[2] = coopController.CoopState.WarpsSecondCastle.value;

			ushort shortcuts = 0;
			for (ushort i = 0; i < coopController.CoopState.shortcuts.Length; i++)
			{
				if (coopController.CoopState.shortcuts[i].status)
				{
					shortcuts |= (ushort) (1 << i);
				}
			}

			int relicsNumber = 0;
			for (int i = 0; i < coopController.CoopState.relics.Length; i++)
			{
				if (coopController.CoopState.relics[i].status)
				{
					relicsNumber |= (1 << i);
				}
			}

			int bossesNumber = 0;
			int bossCount = coopController.CoopState.bosses.Length - 1;

			for (int i = 0; i < bossCount; i++)
			{
				uint timeAttackValue = sotnApi.GameApi.GetTimeAttack(
					(SotnApi.Constants.Values.Game.Enums.Times) (i + 1)
				);

				if (timeAttackValue > 0)
				{
					bossesNumber |= (1 << i);
				}
			}

			fixed (byte* buffer = data13)
			{
				*((ushort*) (buffer + 3)) = shortcuts;
				*((int*) (buffer + 5)) = relicsNumber;
				*((int*) (buffer + 9)) = bossesNumber;
			}

			coopController.SendData(data13);

			for (int i = 0; i < bossCount; i++)
			{
				uint timeAttackValue = sotnApi.GameApi.GetTimeAttack(
					(SotnApi.Constants.Values.Game.Enums.Times) (i + 1)
				);

				if (timeAttackValue > 0)
				{
					byte[] packet = new byte[6];
					packet[0] = (byte) MessageType.BossDefeat;
					packet[1] = (byte) i;
					Array.Copy(BitConverter.GetBytes(timeAttackValue), 0, packet, 2, 4);

					coopController.SendData(packet);
				}
			}
		}

		private void SendBosses()
		{
			for (int i = 0; i < coopController.CoopState.bosses.Length; i++)
			{
				if (coopController.CoopState.bosses[i].updated &&
					coopController.CoopState.bosses[i].status)
				{
					uint timeAttackValue = sotnApi.GameApi.GetTimeAttack(
						(SotnApi.Constants.Values.Game.Enums.Times) (i + 1)
					);

					byte[] packet = new byte[6];
					packet[0] = (byte) MessageType.BossDefeat;
					packet[1] = (byte) i;
					Array.Copy(BitConverter.GetBytes(timeAttackValue), 0, packet, 2, 4);

					coopController.SendData(packet);

					coopController.CoopState.bosses[i].updated = false;
				}
			}
		}

	}
}
