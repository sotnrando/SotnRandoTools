using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;
using Newtonsoft.Json;
using SotnApi.Constants.Values.Game;
using SotnApi.Constants.Values.Game.Enums;
using SotnApi.Main;
using SotnRandoTools.Configuration;
using SotnRandoTools.Constants;
using SotnRandoTools.RandoTracker.Models;
using SotnRandoTools.Services;

namespace SotnRandoTools
{
	// MapForm now inherits ToolFormBase so BizHawk can manage its lifecycle
	public partial class MapForm : Form
	{
		// Dependencies injected from the main tool
		private readonly ToolConfig _toolConfig;
		private readonly SotnApi.Main.SotnApi _sotnApi;
		private readonly IJoypadApi _joypad;
		private readonly NotificationService _notificationService;
		private int _mapUpdateCounter = 0;

		// Map dimensions (fixed 64x64 tiles)
		private const int MapWidth = 64;
		private const int MapHeight = 64;

		// Two separate maps: one for each castle
		private readonly int[,] _recMap1 = new int[MapWidth, MapHeight];
		private readonly int[,] _recMap2 = new int[MapWidth, MapHeight];

		// Save/warp tiles separated by castle to prevent cross‑castle contamination
		private readonly HashSet<(int x, int y)> _saveRoomTiles1 = new();
		private readonly HashSet<(int x, int y)> _saveRoomTiles2 = new();
		private readonly HashSet<(int x, int y)> _warpRoomTiles1 = new();
		private readonly HashSet<(int x, int y)> _warpRoomTiles2 = new();
		private readonly Dictionary<(int x, int y), string> _locationNames1 = new();
		private readonly Dictionary<(int x, int y), string> _locationNames2 = new();

		// New cross-network companion data structures
		private readonly ConcurrentDictionary<byte, (int x, int y, int castle)> _remotePlayerLocations = new();
		private readonly ConcurrentDictionary<byte, HashSet<(int x, int y)>> _remoteExploredMap1 = new();
		private readonly ConcurrentDictionary<byte, HashSet<(int x, int y)>> _remoteExploredMap2 = new();

		// Map scale (5px or 10px tiles)
		private int _mapSize = 2;

		// Current castle (1 = normal, 2 = inverted)
		private int _curCastle = 1;

		// Last tile visited (for pink cursor)
		private int _previousX = -10;
		private int _previousY = -10;
		private int _lastTileX = -1;
		private int _lastTileY = -1;

		// Offsets for aligning the castle images
		private int _castle1OffsetX = 0;
		private int _castle1OffsetY = -3;
		private int _castle2OffsetX = 0;
		private int _castle2OffsetY = -10;

		// UI elements
		private PictureBox _pbLiveMap;
		private Bitmap _mapBitmap;
		private Label _lblLocationInfo;
		private Label _lblCheckCounter;

		// Tile types
		private const int TileTrail = 1;
		private const int TileCheck = 2;
		private const int TileSave = 3;
		private const int TileWarp = 4;

		// Extension name (Classic, Guarded, etc.)
		private string _currentExtension = "Unknown";

		// Prevents updates after closing
		private bool _isClosing = false;
		private HashSet<(int x, int y)> _checkTiles1 = new();
		private HashSet<(int x, int y)> _checkTiles2 = new();

		internal MapForm(
			ToolConfig toolConfig,
			SotnApi.Main.SotnApi sotnApi,
			IJoypadApi joypad,
			NotificationService notificationService,
			RandoTracker.Tracker? tracker)
		{
			// Designer initialization
			InitializeComponent();

			// Store dependencies
			_toolConfig = toolConfig;
			_sotnApi = sotnApi;
			_joypad = joypad;
			_notificationService = notificationService;

			// Detect extension from RAM/preset
			_currentExtension = DetectExtensionFromGame();

			// Build top info panel
			var infoPanel = new Panel
			{
				Dock = DockStyle.Top,
				Height = 26,
				BackColor = Color.Black
			};

			// Location label (shows tile + pixel coords)
			_lblLocationInfo = new Label
			{
				Dock = DockStyle.Fill,
				ForeColor = Color.White,
				BackColor = Color.Transparent,
				Text = "Location: ---",
				TextAlign = ContentAlignment.MiddleLeft,
				Padding = new Padding(6, 3, 0, 0)
			};
			infoPanel.Controls.Add(_lblLocationInfo);

			_lblCheckCounter = new Label
			{
				Dock = DockStyle.Right,
				Width = 140,
				ForeColor = Color.White,
				BackColor = Color.Transparent,
				TextAlign = ContentAlignment.MiddleRight,
				Padding = new Padding(0, 3, 6, 0),
				Text = "Checks: 0"
			};
			infoPanel.Controls.Add(_lblCheckCounter);

			var menu = new ContextMenuStrip();
			menu.Items.Add("Copy", null, (s, e) =>
			{
				Clipboard.SetText(_lblLocationInfo.Text);
			});
			_lblLocationInfo.ContextMenuStrip = menu;

			// PictureBox that displays the map bitmap
			_pbLiveMap = new PictureBox
			{
				Dock = DockStyle.Fill,
				BackColor = Color.Black,
				SizeMode = PictureBoxSizeMode.StretchImage
			};

			Controls.Add(_pbLiveMap);
			Controls.Add(infoPanel);

			// Create the bitmap that the map is drawn onto
			_mapBitmap = new Bitmap(640, 510);
			_pbLiveMap.Image = _mapBitmap;

			// Window size
			ClientSize = new Size(600, 478);

			// Load checks, draw castle, draw map
			LoadChecks();
			ChangeCastle();
			DrawCastleProgress();

		}// Detects castle transitions + extension changes
		private void UpdateCastleState()
		{
			var character = _sotnApi.GameApi.CurrentCharacter;
			bool inverted = _sotnApi.GameApi.SecondCastle;

			// Only track Alucard
			if (character != Character.Alucard)
				return;

			int newCastle = inverted ? 2 : 1;

			// If castle changed, redraw everything
			if (newCastle != _curCastle)
			{
				_curCastle = newCastle;
				ChangeCastle();
			}

			// Detect extension changes (Classic, Guarded, etc.)
			string newExt = DetectExtensionFromGame();
			if (newExt != _currentExtension)
			{
				_currentExtension = newExt;
				LoadChecks();
			}
		}

		// Main update loop called every frame
		public void UpdateMapTracker()
		{
			if (_isClosing)
				return;
			if (_sotnApi == null)
				return;

			// Only track movement when Alucard is active and has a hitbox
			if (!_sotnApi.GameApi.InAlucardMode() || !_sotnApi.AlucardApi.HasHitbox())
				return;

			// Prevent intro hallway trail
			int rooms = (int) _sotnApi.AlucardApi.Rooms;
			if (rooms <= 0)
				return;

			uint action = _sotnApi.AlucardApi.Action;

			// UPDATE ONLY EVERY 10 FRAMES
			_mapUpdateCounter++;
			if (_mapUpdateCounter < 10)
				return;
			_mapUpdateCounter = 0;

			UpdateCastleState();

			// Extension changed mid‑run
			string newExt = DetectExtensionFromGame();
			if (newExt != _currentExtension)
			{
				_currentExtension = newExt;
				LoadChecks();
				DrawCastleProgress();
			}

			// Read tile coordinates from RAM
			int castleX = (int) _sotnApi.AlucardApi.MapX;
			int castleY = (int) _sotnApi.AlucardApi.MapY;
			UpdateLocationText(castleX, castleY);

			// If tile hasn't changed, skip
			if (castleX == _lastTileX && castleY == _lastTileY)
				return;

			_lastTileX = castleX;
			_lastTileY = castleY;

			// Convert tile coords → pixel coords
			int size = _mapSize == 2 ? 10 : 5;
			int offsetX = _curCastle == 1 ? _castle1OffsetX : _castle2OffsetX;
			int offsetY = _curCastle == 1 ? _castle1OffsetY : _castle2OffsetY;
			int drawX = castleX * size + offsetX * size;
			int drawY = castleY * size + offsetY * size;

			DrawTrail(drawX, drawY);
		}

		// Marks the current tile as trail/save/warp
		private void DrawTrail(int drawX, int drawY)
		{
			int size = _mapSize == 2 ? 10 : 5;
			int tileX = drawX / size;
			int tileY = (drawY + (size * 3)) / size;

			if (tileX < 0 || tileX >= MapWidth || tileY < 0 || tileY >= MapHeight)
				return;

			int[,] recMap = _curCastle == 1 ? _recMap1 : _recMap2;
			var saveSet = _curCastle == 1 ? _saveRoomTiles1 : _saveRoomTiles2;
			var warpSet = _curCastle == 1 ? _warpRoomTiles1 : _warpRoomTiles2;

			if (saveSet.Contains((tileX, tileY)))
			{
				recMap[tileX, tileY] = TileSave;
			}
			else if (warpSet.Contains((tileX, tileY)))
			{
				recMap[tileX, tileY] = TileWarp;
			}
			else
			{
				bool wasCheck = (_curCastle == 1 && _checkTiles1.Contains((tileX, tileY))) ||
								(_curCastle == 2 && _checkTiles2.Contains((tileX, tileY)));
				if (wasCheck)
				{
					if (_curCastle == 1) _checkTiles1.Remove((tileX, tileY));
					else _checkTiles2.Remove((tileX, tileY));
					UpdateCheckCounter();
				}
				recMap[tileX, tileY] = TileTrail;
			}

			_lastTileX = tileX;
			_lastTileY = tileY;
			DrawCastleProgress();
		}

		// Redraws the entire map bitmap including network companion layers
		private void DrawCastleProgress()
		{
			if (_isClosing)
				return;

			using (var g = Graphics.FromImage(_mapBitmap))
			{
				// Clear background
				g.FillRectangle(Brushes.Black, new Rectangle(0, 0, 640, 510));

				// Pixel‑perfect rendering configurations
				g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
				g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

				int[,] recMap = _curCastle == 1 ? _recMap1 : _recMap2;
				int size = _mapSize == 2 ? 10 : 5;

				// Draw colored base map tiles
				for (int x = 0; x < MapWidth; x++)
				{
					for (int y = 0; y < MapHeight; y++)
					{
						int val = recMap[x, y];
						if (val == 0) continue;

						int drawX = x * size;
						int drawY = y * size - (size * 3);
						Rectangle rect = new Rectangle(drawX, drawY, size, size);

						switch (val)
						{
							case TileTrail:
								using (var brush = new SolidBrush(Color.FromArgb(255, 0, 0, 224)))
									g.FillRectangle(brush, rect);
								break;
							case TileCheck:
								using (var brush = new SolidBrush(Color.FromArgb(255, 0, 255, 0)))
									g.FillRectangle(brush, rect);
								break;
							case TileSave:
								using (var brush = new SolidBrush(Color.Red))
									g.FillRectangle(brush, rect);
								break;
							case TileWarp:
								using (var brush = new SolidBrush(Color.Yellow))
									g.FillRectangle(brush, rect);
								break;
						}
					}
				}

				// Draw castle template images on top
				Image castleImg = _curCastle == 1
					? Properties.Resources.Castle1_Empty_TP
					: Properties.Resources.Castle2_Empty_TP;

				g.DrawImage(castleImg, new Rectangle(0, 0, 320 * _mapSize, 255 * _mapSize));

				// Draw local current tile active cursor (pink)
				if (_lastTileX >= 0 && _lastTileY >= 0)
				{
					int curX = _lastTileX * size;
					int curY = _lastTileY * size - (size * 3);
					using var pink = new SolidBrush(Color.FromArgb(255, 224, 0, 224));
					g.FillRectangle(pink, new Rectangle(curX, curY, size - 1, size - 1));
				}

				// 1. Render where teammates HAVE BEEN (Red dots centered on each history tile)
				var currentRemoteHistoryMap = _curCastle == 1 ? _remoteExploredMap1 : _remoteExploredMap2;

				// Automatically size the dots depending on whether your map is zoomed in or out
				int dotDiameter = size == 10 ? 4 : 2;
				int centerOffset = (size - dotDiameter) / 2;

				using (var teammateTrailBrush = new SolidBrush(Color.OrangeRed))
				{
					foreach (var playerHistory in currentRemoteHistoryMap.Values)
					{
						foreach (var tile in playerHistory)
						{
							// Calculate the base square coordinates of the map tile
							int tileDrawX = tile.x * size;
							int tileDrawY = tile.y * size - (size * 3);

							// Add the offsets to position the dot dead-center inside the tile
							int dotX = tileDrawX + centerOffset;
							int dotY = tileDrawY + centerOffset;

							g.FillEllipse(teammateTrailBrush, new Rectangle(dotX, dotY, dotDiameter, dotDiameter));
						}
					}
				}
				// 2. Render where teammates ARE NOW (Bright Blue coordinate tracker nodes)
				using (var remotePlayerBrush = new SolidBrush(Color.FromArgb(255, 0, 120, 255)))
				{
					foreach (var pos in _remotePlayerLocations.Values)
					{
						// Only evaluate if the teammate is in the exact same castle as you!
						if (pos.castle == _curCastle)
						{
							int targetX = pos.x;
							int targetY = pos.y;

							if (_curCastle == 2)
							{
								// Apply the 7-tile upward offset used by your inverted map grid template layout
								targetY -= 7;
							}

							int remoteCurX = targetX * size;
							int remoteCurY = targetY * size - (size * 3);

							// Canvas bounds protection layer
							if (targetX >= 0 && targetX < MapWidth && targetY >= 0 && targetY < MapHeight)
							{
								g.FillEllipse(remotePlayerBrush, new Rectangle(remoteCurX, remoteCurY, size - 1, size - 1));
							}
						}
					}
				}
			}
			_pbLiveMap.Refresh();
		}// Clears and redraws the castle layout background image
		private void ChangeCastle()
		{
			using (var g = Graphics.FromImage(_mapBitmap))
			{
				g.Clear(Color.Black);
				Image castleImg = _curCastle == 1
					? Properties.Resources.Castle1_Empty_TP
					: Properties.Resources.Castle2_Empty_TP;
				g.DrawImage(castleImg, new Rectangle(0, 0, 320 * _mapSize, 255 * _mapSize));
			}
			DrawCastleProgress();
			_pbLiveMap.Refresh();
		}

		public void HandleRemotePlayerLocation(byte playerId, ushort mapX, ushort mapY)
		{
			if (_isClosing) return;
			if (InvokeRequired)
			{
				Invoke(new Action(() => HandleRemotePlayerLocation(playerId, mapX, mapY)));
				return;
			}

			// Determine the teammate's castle BEFORE saving it
			int teammateCastle = (mapY > 32) ? 2 : 1;

			// Save all three values to the thread-safe tracker
			_remotePlayerLocations[playerId] = ((int) mapX, (int) mapY, teammateCastle);
			DrawCastleProgress();
		}
		public void HandleRemotePlayerHistory(byte castleNum, ushort tileX, ushort tileY)
		{
			if (_isClosing) return;
			if (InvokeRequired)
			{
				Invoke(new Action(() => HandleRemotePlayerHistory(castleNum, tileX, tileY)));
				return;
			}

			int finalTileX = (int) tileX;
			int finalTileY = (int) tileY;

			if (castleNum == 1)
			{
				// Assign to the Normal Castle teammate history collection set
				var historySet = _remoteExploredMap1.GetOrAdd(0, _ => new HashSet<(int x, int y)>());
				historySet.Add((finalTileX, finalTileY));

				// ==========================================
				// TEAMMATE CHECK CLEARING SYSTEM (Castle 1):
				// ==========================================
				// If this tile is currently tracked as an active uncollected check, clear it!
				if (_checkTiles1.Contains((finalTileX, finalTileY)))
				{
					_checkTiles1.Remove((finalTileX, finalTileY));

					// Mark the tile as an explored/empty trail in the underlying map grid array layout
					// so it changes color from Green (Check) to Empty/Explored
					_recMap1[finalTileX, finalTileY] = TileTrail;

					UpdateCheckCounter();
				}
			}
			else
			{
				// Apply the 7-tile upward offset used by your inverted map layout structure
				int adjustedTileY = finalTileY - 7;

				var historySet = _remoteExploredMap2.GetOrAdd(0, _ => new HashSet<(int x, int y)>());
				historySet.Add((finalTileX, adjustedTileY));

				// ==========================================
				// TEAMMATE CHECK CLEARING SYSTEM (Castle 2):
				// ==========================================
				if (_checkTiles2.Contains((finalTileX, adjustedTileY)))
				{
					_checkTiles2.Remove((finalTileX, adjustedTileY));
					_recMap2[finalTileX, adjustedTileY] = TileTrail;
					UpdateCheckCounter();
				}
			}

			// Redraw the canvas immediately to update map item colors and counter values
			DrawCastleProgress();
		}
		// Loads all checks for the current game extension presets
		private void LoadChecks()
		{
			Array.Clear(_recMap1, 0, _recMap1.Length);
			Array.Clear(_recMap2, 0, _recMap2.Length);

			_saveRoomTiles1.Clear();
			_saveRoomTiles2.Clear();
			_warpRoomTiles1.Clear();
			_warpRoomTiles2.Clear();

			RelicChecks();
			SaveRoomChecks();
			WarpRoomChecks();

			switch (_currentExtension)
			{
				case "Classic":
					KeyItemChecks();
					break;
				case "Guarded":
					KeyItemChecks();
					GuardedChecks();
					break;
				case "GuardedPlus":
					KeyItemChecks();
					GuardedChecks();
					GuardedPlusChecks();
					break;
				case "Extended":
					KeyItemChecks();
					GuardedChecks();
					GuardedPlusChecks();
					private_ExtendedAndScenic();
					break;
				case "Equipment":
					KeyItemChecks();
					GuardedChecks();
					GuardedPlusChecks();
					EquipmentChecks();
					ExtendedChecks();
					break;
				case "Scenic":
					KeyItemChecks();
					GuardedChecks();
					GuardedPlusChecks();
					EquipmentChecks();
					ExtendedChecks();
					ScenicChecks();
					break;
				default:
					KeyItemChecks();
					GuardedChecks();
					break;
			}
			UpdateCheckCounter();
			DrawCastleProgress();
		}

		private void private_ExtendedAndScenic()
		{
			ExtendedChecks();
			ScenicChecks();
		}

		private void AddCheckpx(int posX, int posY, int castleNum, string name)
		{
			int x = posX / 5;
			int y = (posY + 15) / 5;
			if (castleNum == 2) y -= 7;

			if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight) return;

			if (castleNum == 1)
			{
				_recMap1[x, y] = TileCheck;
				_checkTiles1.Add((x, y));
				_locationNames1[(x, y)] = name;
			}
			else
			{
				_recMap2[x, y] = TileCheck;
				_checkTiles2.Add((x, y));
				_locationNames2[(x, y)] = name;
			}
		}

		private void AddSaveRoom(int posX, int posY, int castleNum)
		{
			int x = posX / 5;
			int y = (posY + 15) / 5;
			if (castleNum == 2) y -= 7;

			if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight) return;

			if (castleNum == 1)
			{
				_saveRoomTiles1.Add((x, y));
				_recMap1[x, y] = 0;
			}
			else
			{
				_saveRoomTiles2.Add((x, y));
				_recMap2[x, y] = 0;
			}
		}

		private void AddWarpRoom(int posX, int posY, int castleNum)
		{
			int x = posX / 5;
			int y = (posY + 15) / 5;
			if (castleNum == 2) y -= 7;

			if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight) return;

			if (castleNum == 1)
			{
				_warpRoomTiles1.Add((x, y));
				_recMap1[x, y] = 0;
			}
			else
			{
				_warpRoomTiles2.Add((x, y));
				_recMap2[x, y] = 0;
			}
		}

		private string DetectExtensionFromGame()
		{
			string presetName = _sotnApi.GameApi.ReadPresetName();
			byte presetByte = _sotnApi.GameApi.PresetByte;
			var (ramExtension, ramComplexity, ramValid) = DecodePresetByte(presetByte);

			string normalizedPreset = Regex.Replace(presetName, "[^A-Za-z0-9-]", "");
			string presetFilePath = Path.Combine(Paths.PresetPath, normalizedPreset + ".json");
			Preset? presetObj = null;

			if (File.Exists(presetFilePath))
			{
				try { presetObj = JsonConvert.DeserializeObject<Preset>(File.ReadAllText(presetFilePath)); }
				catch { }
			}

			if (presetName == "tournament" || string.IsNullOrEmpty(presetName)) presetName = "custom";
			if (!ramValid && presetObj != null) return presetObj.Metadata.Extension;

			return ramExtension;
		}

		private (string extensionName, string complexityText, bool valid) DecodePresetByte(byte value)
		{
			int extDetect = (value & 0xF0) >> 4;
			int compDetect = (value & 0x0F);
			string extension = extDetect switch
			{
				0x0 => "Classic",
				0x1 => "Guarded",
				0x2 => "GuardedPlus",
				0x3 => "Extended",
				0x4 => "Equipment",
				0x5 => "Scenic",
				_ => $"Unknown{extDetect:x3}"
			};
			return (extension, compDetect.ToString(), extDetect >= 0 && extDetect <= 5 && compDetect != 0);
		}

		private void UpdateLocationText(int castleX, int castleY)
		{
			if (castleX < 0 || castleX >= MapWidth || castleY < 0 || castleY >= MapHeight)
			{
				_lblLocationInfo.Text = "Location: Out of bounds";
				return;
			}
			int posX = castleX * 5;
			int posY = castleY * 5 - 15;
			string castle = _curCastle == 1 ? "1" : "2";

			int lookupY = castleY;
			if (_curCastle == 2) lookupY -= 7;

			string locationName = "";
			if (_curCastle == 1 && _locationNames1.TryGetValue((castleX, lookupY), out var name1))
				locationName = name1;
			else if (_curCastle == 2 && _locationNames2.TryGetValue((castleX, lookupY), out var name2))
				locationName = name2;

			_lblLocationInfo.Text = $"({posX}, {posY}, {castle}) {locationName}";
		}

		private void UpdateCheckCounter()
		{
			int left = _checkTiles1.Count + _checkTiles2.Count;
			_lblCheckCounter.Text = $"Checks Left: {left}";
		}

		private void RelicChecks()
		{
			AddCheckpx(15, 175, 1, "Power of Wolf");
			AddCheckpx(95, 165, 1, "Cube of Zoe");
			AddCheckpx(75, 150, 1, "Skill of Wolf");
			AddCheckpx(65, 120, 1, "Bat Card");
			AddCheckpx(125, 140, 1, "Spirit Orb");
			AddCheckpx(170, 100, 1, "Gravity Boots");
			AddCheckpx(105, 95, 1, "Form of Mist");
			AddCheckpx(80, 65, 1, "Echo of Bat");
			AddCheckpx(100, 75, 1, "Sword Card");
			AddCheckpx(155, 40, 1, "Leap Stone");
			AddCheckpx(155, 30, 1, "Power of Mist");
			AddCheckpx(195, 20, 1, "Ghost Card");
			AddCheckpx(295, 40, 1, "Fire of Bat");
			AddCheckpx(305, 75, 1, "Soul of Wolf");
			AddCheckpx(295, 75, 1, "Faerie Scroll");
			AddCheckpx(260, 75, 1, "Fairy Card");
			AddCheckpx(240, 90, 1, "Soul of Bat");
			AddCheckpx(245, 85, 1, "Jewel of Open");
			AddCheckpx(275, 190, 1, "Holy Symbol");
			AddCheckpx(40, 195, 1, "Merman Statue");
			AddCheckpx(145, 205, 1, "Demon Card");

			AddCheckpx(115, 250, 2, "Ring of Vlad");
			AddCheckpx(25, 185, 2, "Tooth of Vlad");
			AddCheckpx(40, 95, 2, "Force of Echo");
			AddCheckpx(160, 100, 2, "Eye of Vlad");
			AddCheckpx(230, 50, 2, "Gas Cloud");
			AddCheckpx(220, 220, 2, "Rib of Vlad");
			AddCheckpx(195, 235, 2, "Heart of Vlad");
		}
		private void KeyItemChecks()
		{
			AddCheckpx(225, 150, 1, "Gold Ring");
			AddCheckpx(40, 60, 1, "Silver Ring");
			AddCheckpx(160, 140, 1, "Holy Glasses");
			AddCheckpx(205, 240, 1, "Spike Breaker");
		}
		private void GuardedChecks()
		{
			AddCheckpx(85, 235, 1, "Mormegil");
			AddCheckpx(200, 175, 1, "Crystal Cloak");

			AddCheckpx(115, 110, 2, "Dark Blade");
			AddCheckpx(215, 190, 2, "Trio");
			AddCheckpx(250, 165, 2, "Ring of Arcana");
		}
		private void GuardedPlusChecks()
		{
			AddCheckpx(65, 210, 2, "Badelaire");
			AddCheckpx(70, 200, 2, "Forbidden Library Opal");
		}

		private void EquipmentChecks()
		{
			AddCheckpx( 25, 175, 1, "Holy Mail");
			AddCheckpx( 50, 190, 1, "Jewel Sword");
			AddCheckpx( 50, 130, 1, "Cloth Cape");
			AddCheckpx( 80, 140, 1, "Sunglasses");
			AddCheckpx(295, 100, 1, "Gladius");
			AddCheckpx(245,  90, 1, "Bronze Cuirass");
			AddCheckpx(250,  75, 1, "Holy Rod");
			AddCheckpx(230,  90, 1, "Library Onyx");
			AddCheckpx(195,  25, 1, "Falchion");
			AddCheckpx( 20, 110, 1, "Ankh of Life");
			AddCheckpx( 40,  90, 1, "Morningstar");
			AddCheckpx(135,  35, 1, "Cutlass");
			AddCheckpx(160,  95, 1, "Olrox Onyx");
			AddCheckpx(150,  60, 1, "Estoc");
			AddCheckpx(165,  75, 1, "Olrox Garnet");
			AddCheckpx( 65, 105, 1, "Shield rod");
			AddCheckpx(100, 105, 1, "Blood cloak");
			AddCheckpx( 95,  85, 1, "Holy sword");
			AddCheckpx( 70,  95, 1, "Knight Shield");
			AddCheckpx(175, 120, 1, "Bandanna");
			AddCheckpx(120, 180, 1, "Secret Boots");
			AddCheckpx(200, 195, 1, "Knuckle Duster");
			AddCheckpx(225, 190, 1, "Caverns Onyx");
			AddCheckpx(155, 225, 1, "Combat Knife");
			AddCheckpx(140, 235, 1, "Blood stone");
			AddCheckpx(120, 235, 1, "Icebrand");
			AddCheckpx(115, 235, 1, "Walk Armor");

			AddCheckpx(150, 270, 2, "Bastard sword");
			AddCheckpx(140, 270, 2, "Royal Cloak");
			AddCheckpx(160, 245, 2, "Sword of Dawn");
			AddCheckpx(120, 245, 2, "Lightning Mail");
			AddCheckpx( 20, 245, 2, "Dragon Helm");
			AddCheckpx( 70, 230, 2, "Sunstone");
			AddCheckpx(220, 245, 2, "Talwar");
			AddCheckpx(150, 210, 2, "Alucard Mail");
			AddCheckpx(155, 190, 2, "Sword of Hador");
			AddCheckpx(220, 200, 2, "Fury Plate");
			AddCheckpx(235, 145, 2, "Goddess Shield");
			AddCheckpx( 20, 165, 2, "Shotel");
			AddCheckpx(140, 165, 2, "Reverse Caverns Diamond");
			AddCheckpx(205, 115, 2, "Reverse Caverns Garnet");
			AddCheckpx(275,  90, 2, "Alucard Shield");
			AddCheckpx(170,  80, 2, "Alucard Sword");
			AddCheckpx(195,  50, 2, "Necklace of J");
			AddCheckpx(200,  50, 2, "Floating Catacombs Diamond");
			AddCheckpx(215, 115, 2, "Talisman");
		}
		private void ExtendedChecks()
		{
			AddCheckpx( 80, 155, 1, "Alchemy Labs Cannon");
			AddCheckpx(170, 110, 1, "Alucart Sword");
			AddCheckpx(295, 120, 1, "Jewel Knuckles");
			AddCheckpx(275,  50, 1, "Bekatowa");
			AddCheckpx(245,  55, 1, "Gold Plate");
			AddCheckpx(175,  15, 1, "Platinum Mail");
			AddCheckpx( 10, 120, 1, "Mystic Pendant");
			AddCheckpx( 50,  85, 1, "Goggles");
			AddCheckpx( 70,  45, 1, "Silver Plate");
			AddCheckpx(190, 175, 1, "Nunchaku");
			AddCheckpx(185, 190, 1, "Ring of Ares");

			AddCheckpx(105, 245, 2, "Moon Rod");
			AddCheckpx( 40, 235, 2, "Luminus");
			AddCheckpx(270, 225, 2, "Reverse Silver Ring");
			AddCheckpx(215, 180, 2, "Reverse Blood Cloak / Gram");
			AddCheckpx(255, 120, 2, "Katana");
			AddCheckpx(130, 140, 2, "Lone Imp Room");
			AddCheckpx(190,  90, 2, "Osafune Katana");
			AddCheckpx(265,  95, 2, "Beryl Circlet");
			AddCheckpx( 75, 195, 2, "Staurolite");
		}
		private void ScenicChecks()
		{
			AddCheckpx(300, 130, 1, "Telescope");
			AddCheckpx(255,  30, 1, "Cloaked Knight");
			AddCheckpx(125, 195, 1, "Hidden Waterfall Room");
			AddCheckpx( 80,  90, 1, "Confessional");
			AddCheckpx( 95, 105, 1, "Bath Room");

			AddCheckpx(120, 270, 2, "Reverse Ghost Card");
			AddCheckpx(250, 180, 2, "Reverse Shield Rod");
			AddCheckpx(145, 175, 2, "Reverse Alucart Sword");
			AddCheckpx( 85, 180, 2, "Reverse Jewel Switch");
			AddCheckpx(185, 130, 2, "Forbidden Route LC");
			AddCheckpx(135,  95, 2, "Cave Life Apple");
			AddCheckpx(110,  45, 2, "Reverse Spike Breaker");
			AddCheckpx(305, 110, 2, "Reverse Entrance");
		}
		// Save rooms (red tiles)
		private void SaveRoomChecks()
		{
			AddSaveRoom(85, 170, 1);
			AddSaveRoom(25, 180, 1);
			AddSaveRoom(80, 160, 1);
			AddSaveRoom(35, 135, 1);
			AddSaveRoom(75, 120, 1);
			AddSaveRoom(145, 120, 1);
			AddSaveRoom(125, 100, 1);
			AddSaveRoom(10, 125, 1);
			AddSaveRoom(155, 70, 1);
			AddSaveRoom(155, 120, 1);
			AddSaveRoom(200, 120, 1);
			AddSaveRoom(215, 150, 1);
			AddSaveRoom(200, 170, 1);
			AddSaveRoom(140, 190, 1);
			AddSaveRoom(150, 235, 1);
			AddSaveRoom(110, 235, 1);
			AddSaveRoom(255, 80, 1);
			AddSaveRoom(275, 105, 1);
			AddSaveRoom(300, 50, 1);
			AddSaveRoom(195, 30, 1);
			AddSaveRoom(145, 50, 1);

			AddSaveRoom(170, 235, 2);
			AddSaveRoom(120, 255, 2);
			AddSaveRoom(15, 235, 2);
			AddSaveRoom(60, 205, 2);
			AddSaveRoom(40, 180, 2);
			AddSaveRoom(115, 165, 2);
			AddSaveRoom(115, 115, 2);
			AddSaveRoom(175, 95, 2);
			AddSaveRoom(165, 50, 2);
			AddSaveRoom(205, 50, 2);
			AddSaveRoom(290, 105, 2);
			AddSaveRoom(230, 115, 2);
			AddSaveRoom(235, 125, 2);
			AddSaveRoom(240, 165, 2);
			AddSaveRoom(280, 150, 2);
			AddSaveRoom(305, 160, 2);
			AddSaveRoom(160, 215, 2);
			AddSaveRoom(190, 185, 2);
			AddSaveRoom(245, 185, 2);
			AddSaveRoom(170, 165, 2);
			AddSaveRoom(160, 165, 2);
			AddSaveRoom(100, 135, 2);
		}

		// Warp rooms (yellow tiles)
		private void WarpRoomChecks()
		{
			AddWarpRoom(185, 90, 1);
			AddWarpRoom(75, 175, 1);
			AddWarpRoom(175, 205, 1);
			AddWarpRoom(295, 70, 1);
			AddWarpRoom(200, 45, 1);

			AddWarpRoom(115, 240, 2);
			AddWarpRoom(20, 215, 2);
			AddWarpRoom(140, 80, 2);
			AddWarpRoom(240, 110, 2);
			AddWarpRoom(130, 195, 2);
		}
	}
}