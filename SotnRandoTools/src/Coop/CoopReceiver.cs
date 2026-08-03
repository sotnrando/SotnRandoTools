using System;
using System.Collections.Concurrent;
using SotnApi.Constants.Values.Alucard;
using SotnApi.Constants.Values.Alucard.Enums;
using SotnApi.Constants.Values.Game;
using SotnApi.Interfaces;
using SotnApi.Constants.Addresses;
using SotnRandoTools.Configuration.Interfaces;
using SotnRandoTools.Coop.Enums;
using SotnRandoTools.Coop.Interfaces;
using SotnRandoTools.Services;

namespace SotnRandoTools.Coop
{
	internal sealed class CoopReceiver : ICoopReceiver
	{
		private readonly IToolConfig toolConfig;
		private readonly ISotnApi sotnApi;
		private readonly INotificationService notificationService;
		private readonly ICoopController coopController;

		public CoopReceiver(IToolConfig toolConfig, ISotnApi sotnApi, INotificationService notificationService, ICoopController coopController)
		{
			this.toolConfig = toolConfig ?? throw new ArgumentNullException(nameof(toolConfig));
			this.sotnApi = sotnApi ?? throw new ArgumentNullException(nameof(sotnApi));
			this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
			this.coopController = coopController ?? throw new ArgumentNullException(nameof(coopController));
			MessageQueue = new();
		}

		public ConcurrentQueue<byte[]> MessageQueue { get; set; }

		private void ProcessMessage(byte[] data)
		{
			try
			{
				MessageType type = (MessageType) data[0];
				ushort index = BitConverter.ToUInt16(data, 1);
				ushort index2 = BitConverter.ToUInt16(data, 3);
				byte indexByte = data[1];
				byte dataByte = 0;
				if (data.Length > 2)
				{
					dataByte = data[2];
				}
				switch (type)
				{
					case MessageType.Relic:
						if (!sotnApi.AlucardApi.HasRelic((Relic) indexByte))
						{
							sotnApi.AlucardApi.GrantRelic((Relic) indexByte);
							coopController.CoopState.relics[indexByte].status = true;
							notificationService.AddMessage(Equipment.Relics[indexByte]);
							notificationService.PlayAlert();
						}
						break;
					case MessageType.Location:
						sotnApi.GameApi.SetRoomToVisited(SotnApi.Constants.Addresses.Game.MapStart + index);
						coopController.CoopState.locations[index2].status = true;
						sotnApi.AlucardApi.Rooms++;
						break;
					case MessageType.Item:
						sotnApi.AlucardApi.GrantItemByName(Equipment.Items[index]);
						notificationService.AddMessage(Equipment.Items[index]);
						notificationService.PlayAlert();
						break;
					case MessageType.WarpFirstCastle:
						sotnApi.AlucardApi.WarpsFirstCastle |= indexByte;
						coopController.CoopState.WarpsFirstCastle.value = (byte) sotnApi.AlucardApi.WarpsFirstCastle;
						notificationService.AddMessage($"Received warp: {(Warp) indexByte}");
						break;
					case MessageType.WarpSecondCastle:
						sotnApi.AlucardApi.WarpsSecondCastle |= indexByte;
						coopController.CoopState.WarpsSecondCastle.value = (byte) sotnApi.AlucardApi.WarpsSecondCastle;
						notificationService.AddMessage($"Received warp: Inverted {(Warp) indexByte}");
						break;
					case MessageType.Shortcut:
						if (index > Enum.GetNames(typeof(Shortcut)).Length - 1)
						{
							DecodeShortcuts(index);
						}
						else
						{
							DecodeShortcut((Shortcut) index);
						}
						coopController.CoopState.shortcuts[index].status = true;
						break;
					case MessageType.SynchRequest:
						coopController.SynchRequested = true;
						notificationService.AddMessage("Received Synch Request");
						break;
					case MessageType.SynchAll:
						DecodeSynch(data);
						notificationService.AddMessage("Received Synch All");
						break;
					case MessageType.PlayerCoords:
						{
							if (data.Length >= 6)
							{
								byte playerId = data[1];
								ushort xCoord = BitConverter.ToUInt16(data, 2);
								ushort yCoord = BitConverter.ToUInt16(data, 4);

								coopController.UpdatePlayerLocation(playerId, xCoord, yCoord);
							}
						}
						break;
					case MessageType.RoomHistory:
						{
							if (data.Length >= 6)
							{
								byte teammateCastle = data[1];
								ushort tileX = BitConverter.ToUInt16(data, 2);
								ushort tileY = BitConverter.ToUInt16(data, 4);

								coopController.UpdatePlayerHistory(teammateCastle, tileX, tileY);
							}
						}
						break;
					case MessageType.BossDefeat:
						HandleBossDefeat(data);
						break;
					default:
						break;
				}
			}
			catch (Exception)
			{
				return;
			}
		}

		public void Update()
		{
			bool insideMarbleGalleryDoorRooms =
				(sotnApi.GameApi.Room == Various.MarbleGalleryDoorToCavernsRoom ||
				 sotnApi.GameApi.Room == Various.MarbleGalleryBlueDoorRoom) &&
				(sotnApi.GameApi.Area == Various.MarbleGalleryArea);

			if (!sotnApi.GameApi.InAlucardMode() || insideMarbleGalleryDoorRooms)
			{
				return;
			}
			if (!MessageQueue.IsEmpty && MessageQueue.TryDequeue(out byte[] data))
			{
				ProcessMessage(data);
			}
		}

		private void DecodeShortcut(Shortcut shortcut)
		{
			switch (shortcut)
			{
				case Shortcut.OuterWallElevator:
					sotnApi.AlucardApi.OuterWallElevator = true;
					break;
				case Shortcut.AlchemyElevator:
					sotnApi.AlucardApi.AlchemyElevator = true;
					break;
				case Shortcut.EntranceToMarble:
					sotnApi.AlucardApi.EntranceToMarble = true;
					break;
				case Shortcut.ChapelStatue:
					sotnApi.AlucardApi.ChapelStatue = true;
					break;
				case Shortcut.ColosseumElevator:
					sotnApi.AlucardApi.ColosseumElevator = true;
					break;
				case Shortcut.ColosseumToChapel:
					sotnApi.AlucardApi.ColosseumToChapel = true;
					break;
				case Shortcut.MarbleBlueDoor:
					sotnApi.AlucardApi.MarbleBlueDoor = true;
					break;
				case Shortcut.CavernsSwitchAndBridge:
					sotnApi.AlucardApi.CavernsSwitchAndBridge = true;
					break;
				case Shortcut.EntranceToCaverns:
					sotnApi.AlucardApi.EntranceToCaverns = true;
					break;
				case Shortcut.EntranceWarp:
					sotnApi.AlucardApi.EntranceWarp = true;
					break;
				case Shortcut.FirstClockRoomDoor:
					sotnApi.AlucardApi.FirstClockRoomDoor = true;
					break;
				case Shortcut.SecondClockRoomDoor:
					sotnApi.AlucardApi.SecondClockRoomDoor = true;
					break;
				case Shortcut.FirstDemonButton:
					sotnApi.AlucardApi.FirstDemonButton = true;
					break;
				case Shortcut.SecondDemonButton:
					sotnApi.AlucardApi.SecondDemonButton = true;
					break;
				case Shortcut.KeepStairs:
					sotnApi.AlucardApi.KeepStairs = true;
					break;
				default:
					return;
			}
			notificationService.AddMessage(Constants.CoOp.ShortcutNames[(int) shortcut]);
		}

		private void DecodeShortcuts(int flags)
		{
			if ((flags & (int) ShortcutFlags.OuterWallElevator) == (int) ShortcutFlags.OuterWallElevator)
			{
				sotnApi.AlucardApi.OuterWallElevator = true;
				coopController.CoopState.shortcuts[0].status = true;
			}
			if ((flags & (int) ShortcutFlags.AlchemyElevator) == (int) ShortcutFlags.AlchemyElevator)
			{
				sotnApi.AlucardApi.AlchemyElevator = true;
				coopController.CoopState.shortcuts[1].status = true;
			}
			if ((flags & (int) ShortcutFlags.EntranceToMarble) == (int) ShortcutFlags.EntranceToMarble)
			{
				sotnApi.AlucardApi.EntranceToMarble = true;
				coopController.CoopState.shortcuts[2].status = true;
			}
			if ((flags & (int) ShortcutFlags.ChapelStatue) == (int) ShortcutFlags.ChapelStatue)
			{
				sotnApi.AlucardApi.ChapelStatue = true;
				coopController.CoopState.shortcuts[3].status = true;
			}
			if ((flags & (int) ShortcutFlags.ColosseumElevator) == (int) ShortcutFlags.ColosseumElevator)
			{
				sotnApi.AlucardApi.ColosseumElevator = true;
				coopController.CoopState.shortcuts[4].status = true;
			}
			if ((flags & (int) ShortcutFlags.ColosseumToChapel) == (int) ShortcutFlags.ColosseumToChapel)
			{
				sotnApi.AlucardApi.ColosseumToChapel = true;
				coopController.CoopState.shortcuts[5].status = true;
			}
			if ((flags & (int) ShortcutFlags.MarbleBlueDoor) == (int) ShortcutFlags.MarbleBlueDoor)
			{
				sotnApi.AlucardApi.MarbleBlueDoor = true;
				coopController.CoopState.shortcuts[6].status = true;
			}
			if ((flags & (int) ShortcutFlags.CavernsSwitchAndBridge) == (int) ShortcutFlags.CavernsSwitchAndBridge)
			{
				sotnApi.AlucardApi.CavernsSwitchAndBridge = true;
				coopController.CoopState.shortcuts[7].status = true;
			}
			if ((flags & (int) ShortcutFlags.EntranceToCaverns) == (int) ShortcutFlags.EntranceToCaverns)
			{
				sotnApi.AlucardApi.EntranceToCaverns = true;
				coopController.CoopState.shortcuts[8].status = true;
			}
			if ((flags & (int) ShortcutFlags.EntranceWarp) == (int) ShortcutFlags.EntranceWarp)
			{
				sotnApi.AlucardApi.EntranceWarp = true;
				coopController.CoopState.shortcuts[9].status = true;
			}
			if ((flags & (int) ShortcutFlags.FirstClockRoomDoor) == (int) ShortcutFlags.FirstClockRoomDoor)
			{
				sotnApi.AlucardApi.FirstClockRoomDoor = true;
				coopController.CoopState.shortcuts[10].status = true;
			}
			if ((flags & (int) ShortcutFlags.SecondClockRoomDoor) == (int) ShortcutFlags.SecondClockRoomDoor)
			{
				sotnApi.AlucardApi.SecondClockRoomDoor = true;
				coopController.CoopState.shortcuts[11].status = true;
			}
			if ((flags & (int) ShortcutFlags.FirstDemonButton) == (int) ShortcutFlags.FirstDemonButton)
			{
				sotnApi.AlucardApi.FirstDemonButton = true;
				coopController.CoopState.shortcuts[12].status = true;
			}
			if ((flags & (int) ShortcutFlags.SecondDemonButton) == (int) ShortcutFlags.SecondDemonButton)
			{
				sotnApi.AlucardApi.SecondDemonButton = true;
				coopController.CoopState.shortcuts[13].status = true;
			}
			if ((flags & (int) ShortcutFlags.KeepStairs) == (int) ShortcutFlags.KeepStairs)
			{
				sotnApi.AlucardApi.KeepStairs = true;
				coopController.CoopState.shortcuts[14].status = true;
			}
		}

		private void DecodeSynch(byte[] data)
		{
			sotnApi.AlucardApi.WarpsFirstCastle |= data[1];
			coopController.CoopState.WarpsFirstCastle.value = (byte) sotnApi.AlucardApi.WarpsFirstCastle;
			sotnApi.AlucardApi.WarpsSecondCastle |= data[2];
			coopController.CoopState.WarpsSecondCastle.value = (byte) sotnApi.AlucardApi.WarpsSecondCastle;

			ushort shortcut = BitConverter.ToUInt16(data, 3);
			DecodeShortcuts(shortcut);

			int encodedRelics = BitConverter.ToInt32(data, 5);
			int relicCount = Enum.GetValues(typeof(Relic)).Length;
			for (int i = 0; i < relicCount; i++)
			{
				int flag = (1 << i);
				if ((encodedRelics & flag) == flag)
				{
					sotnApi.AlucardApi.GrantRelic((Relic) i);
					coopController.CoopState.relics[i].status = true;
				}
			}

			if (data.Length >= 13)
			{
				int encodedBosses = BitConverter.ToInt32(data, 9);
				int bossCount = coopController.CoopState.bosses.Length;

				var memApiProperty = sotnApi.GetType().GetProperty("MemAPI");
				if (memApiProperty is not null)
				{
					var memApiInstance = memApiProperty.GetValue(sotnApi);
					var writeU32Method = memApiInstance?.GetType().GetMethod("WriteU32", new Type[] { typeof(long), typeof(uint) });

					if (writeU32Method != null)
					{
						for (int i = 0; i < bossCount - 1; i++)
						{
							int flag = (1 << i);
							if ((encodedBosses & flag) == flag)
							{
								coopController.CoopState.bosses[i].status = true;

								long bossTimeAttackAddress = TimeAttack.Times[i];
								writeU32Method.Invoke(memApiInstance, new object[] { bossTimeAttackAddress, (uint) 1 });
							}
						}
					}
				}
			}
		}
		private void HandleBossDefeat(byte[] data)
		{
			int bossIndex = data[1];

			if (bossIndex < 0 || bossIndex >= coopController.CoopState.bosses.Length)
				return;

			// Extract the 4-byte Time Attack value sent by the killer
			uint timeAttackValue = BitConverter.ToUInt32(data, 2);

			// Update coop state
			coopController.CoopState.bosses[bossIndex].status = true;
			coopController.CoopState.bosses[bossIndex].updated = false;

			// Write the killer's exact time attack value using your new API function
			sotnApi.GameApi.SetTimeAttack(
				(SotnApi.Constants.Values.Game.Enums.Times) (bossIndex + 1),
				timeAttackValue
			);

			// Notification
			string bossName = ((SotnApi.Constants.Values.Game.Enums.Times) (bossIndex + 1)).ToString();
			notificationService.AddMessage($"{bossName} Defeated!");
			notificationService.PlayAlert();

			coopController.InvokeBossDefeated(bossIndex, bossName);
		}

	}
}
