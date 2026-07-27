using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using BizHawk.Client.Common;
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
	public partial class MapForm : Form
	{
		private readonly ToolConfig _toolConfig;
		private readonly SotnApi.Main.SotnApi _sotnApi;
		private readonly IJoypadApi _joypad;
		private readonly NotificationService _notificationService;

		private const int MapWidth = 64;
		private const int MapHeight = 64;

		private readonly int[,] _recMap1 = new int[MapWidth, MapHeight];
		private readonly int[,] _recMap2 = new int[MapWidth, MapHeight];
		private readonly HashSet<(int x, int y)> _saveRoomTiles = new();
		private readonly HashSet<(int x, int y)> _warpRoomTiles = new();

		private int _mapSize = 2;
		private int _curCastle = 1;

		private int _previousX = -10;
		private int _previousY = -10;
		private int _lastTileX = -1;
		private int _lastTileY = -1;

		// Adjustable offsets
		private int _castle1OffsetX = 0;
		private int _castle1OffsetY = -3;
		private int _castle2OffsetX = 0;
		private int _castle2OffsetY = -10;

		private PictureBox _pbLiveMap;
		private Bitmap _mapBitmap;

		// 1 = trail, 2 = check, 3 = save room, 4 = warp room
		private const int TileTrail = 1;
		private const int TileCheck = 2;
		private const int TileSave = 3;
		private const int TileWarp = 4;

		private Label _lblLocationInfo;

		// Extension detected internally
		private string _currentExtension = "Unknown";

		internal MapForm(
			ToolConfig toolConfig,
			SotnApi.Main.SotnApi sotnApi,
			IJoypadApi joypad,
			NotificationService notificationService,
			RandoTracker.Tracker? tracker)
		{
			InitializeComponent();

			_toolConfig = toolConfig;
			_sotnApi = sotnApi;
			_joypad = joypad;
			_notificationService = notificationService;

			_currentExtension = DetectExtensionFromGame();

			// Create a panel to hold the location label
			var infoPanel = new Panel
			{
				Dock = DockStyle.Top,
				Height = 26,
				BackColor = Color.Black
			};

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

			_pbLiveMap = new PictureBox
			{
				Dock = DockStyle.Fill,
				BackColor = Color.Black,
				SizeMode = PictureBoxSizeMode.StretchImage
			};

			Controls.Add(_pbLiveMap);
			Controls.Add(infoPanel);

			_mapBitmap = new Bitmap(640, 510);
			_pbLiveMap.Image = _mapBitmap;

			ClientSize = new Size(600, 478);

			LoadChecks();
			ChangeCastle();
			DrawCastleProgress();
		}

		private void UpdateLocationText(int castleX, int castleY)
		{
			// Prevent out-of-range errors
			if (castleX < 0 || castleX >= MapWidth || castleY < 0 || castleY >= MapHeight)
			{
				_lblLocationInfo.Text = "Location: Out of bounds";
				return;
			}

			int size = _mapSize == 2 ? 10 : 5;
			int posX = castleX * 5;
			int posY = castleY * 5 - 15;

			int[,] recMap = _curCastle == 1 ? _recMap1 : _recMap2;
			int val = recMap[castleX, castleY];

			string castle = _curCastle == 1 ? "1" : "2";
			string type = val switch
			{
				TileSave => "SaveRoom",
				TileWarp => "WarpRoom",
				TileCheck => "Check",
				TileTrail => "Trail",
				_ => "Unknown"
			};

			_lblLocationInfo.Text =
				$"{type}: Tile({castleX}, {castleY}) → ({posX}, {posY}, {castle})";
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

			string complexity = compDetect.ToString();
			bool valid = extDetect >= 0 && extDetect <= 5 && compDetect != 0;

			return (extension, complexity, valid);
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
				try
				{
					presetObj = JsonConvert.DeserializeObject<Preset>(File.ReadAllText(presetFilePath));
				}
				catch { }
			}

			if (presetName == "tournament" || string.IsNullOrEmpty(presetName))
				presetName = "custom";

			if (!ramValid && presetObj != null)
				return presetObj.Metadata.Extension;

			return ramExtension;
		}
		private void UpdateCastleState()
		{
			var character = _sotnApi.GameApi.CurrentCharacter;
			bool inverted = _sotnApi.GameApi.SecondCastle;

			if (character != Character.Alucard)
				return;

			int newCastle = inverted ? 2 : 1;

			if (newCastle != _curCastle)
			{
				_curCastle = newCastle;
				ChangeCastle();
			}

			string newExt = DetectExtensionFromGame();
			if (newExt != _currentExtension)
			{
				_currentExtension = newExt;
				LoadChecks();
			}
		}
		public void UpdateMapTracker()
		{
			if (_sotnApi == null)
				return;

			// Skip trail updates until Alucard is controllable
			if (!_sotnApi.GameApi.InAlucardMode() || !_sotnApi.AlucardApi.HasHitbox())
				return;

			// Wait until the player has entered a real room (prevents intro hallway trail)
			int rooms = (int) _sotnApi.AlucardApi.Rooms;
			if (rooms <= 0)
				return;

			uint action = _sotnApi.AlucardApi.Action;

			// Only track controllable movement actions
			bool isMoving =
				action == 0x00 || // idle
				action == 0x01 || // walk
				action == 0x02 || // run
				action == 0x03 || // jump
				action == 0x04 || // fall
				action == 0x05 || // land
				action == 0x06 || // crouch
				action == 0x07 || // crouch-walk
				action == 0x08 || // backdash
				action == 0x09;   // backdash recovery

			if (!isMoving)
				return;

			UpdateCastleState();

			string newExt = DetectExtensionFromGame();
			if (newExt != _currentExtension)
			{
				_currentExtension = newExt;
				LoadChecks();
				DrawCastleProgress();
			}

			int castleX = (int) _sotnApi.AlucardApi.MapX;
			int castleY = (int) _sotnApi.AlucardApi.MapY;
			UpdateLocationText(castleX, castleY);

			if (castleX == _lastTileX && castleY == _lastTileY)
				return;

			_lastTileX = castleX;
			_lastTileY = castleY;

			int size = _mapSize == 2 ? 10 : 5;
			int offsetX = _curCastle == 1 ? _castle1OffsetX : _castle2OffsetX;
			int offsetY = _curCastle == 1 ? _castle1OffsetY : _castle2OffsetY;

			int drawX = castleX * size + offsetX * size;
			int drawY = castleY * size + offsetY * size;

			DrawTrail(drawX, drawY);
		}
		private void DrawTrail(int drawX, int drawY)
		{
			int size = _mapSize == 2 ? 10 : 5;

			// Convert pixel back to tile
			int tileX = drawX / size;
			int tileY = (drawY + (size * 3)) / size;

			// Prevent out-of-range errors
			if (tileX < 0 || tileX >= MapWidth || tileY < 0 || tileY >= MapHeight)
				return;

			int[,] recMap = _curCastle == 1 ? _recMap1 : _recMap2;

			// Save room?
			if (_saveRoomTiles.Contains((tileX, tileY)))
				recMap[tileX, tileY] = TileSave;

			// Warp room?
			else if (_warpRoomTiles.Contains((tileX, tileY)))
				recMap[tileX, tileY] = TileWarp;

			// Normal trail
			else
				recMap[tileX, tileY] = TileTrail;

			// Update last tile
			_lastTileX = tileX;
			_lastTileY = tileY;

			// Redraw everything properly
			DrawCastleProgress();
		}

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
		private void DrawCastleProgress()
		{
			using (var g = Graphics.FromImage(_mapBitmap))
			{
				g.FillRectangle(Brushes.Black, new Rectangle(0, 0, 640, 510));

				// Pixel-perfect settings
				g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
				g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

				int[,] recMap = _curCastle == 1 ? _recMap1 : _recMap2;
				int size = _mapSize == 2 ? 10 : 5;

				// Draw tiles first
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

				// Draw castle image with pixelated upscaling
				Image castleImg = _curCastle == 1
					? Properties.Resources.Castle1_Empty_TP
					: Properties.Resources.Castle2_Empty_TP;

				g.DrawImage(
					castleImg,
					new Rectangle(0, 0, 320 * _mapSize, 255 * _mapSize)
				);

				// --- DRAW CURRENT TILE (pink) BEFORE CASTLE IMAGE ---
				if (_lastTileX >= 0 && _lastTileY >= 0)
				{
					int curX = _lastTileX * size;
					int curY = _lastTileY * size - (size * 3);

					using var pink = new SolidBrush(Color.FromArgb(255, 224, 0, 224));
					g.FillRectangle(pink, new Rectangle(curX, curY, size - 1, size - 1));
				}
			}


			_pbLiveMap.Refresh();
		}

		public void ClearMap(bool fullReset = false)
		{
			if (fullReset)
			{
				Array.Clear(_recMap1, 0, _recMap1.Length);
				Array.Clear(_recMap2, 0, _recMap2.Length);
			}

			_previousX = -10;
			_previousY = -10;
			_lastTileX = -1;
			_lastTileY = -1;

			ChangeCastle();
			DrawCastleProgress();
		}
		private void AddCheckpx(int posX, int posY, int castleNum)
		{
			int x = posX / 5;
			int y = (posY + 15) / 5;

			if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight)
				return;

			if (castleNum == 1)
				_recMap1[x, y] = TileCheck;
			else
				_recMap2[x, y] = TileCheck;
		}

		private void AddSaveRoom(int posX, int posY, int castleNum)
		{
			int x = posX / 5;
			int y = (posY + 15) / 5;

			if (castleNum == 2)
				y -= 7;

			if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight)
				return;

			if (castleNum == 1)
				_recMap1[x, y] = TileSave;
			else
				_recMap2[x, y] = TileSave;

			_saveRoomTiles.Add((x, y));
		}
		private void AddWarpRoom(int posX, int posY, int castleNum)
		{
			int x = posX / 5;
			int y = (posY + 15) / 5;

			if (castleNum == 2)
				y -= 7;

			if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight)
				return;

			if (castleNum == 1)
				_recMap1[x, y] = TileWarp;
			else
				_recMap2[x, y] = TileWarp;

			_warpRoomTiles.Add((x, y));
		}


		private void LoadChecks()
		{
			Array.Clear(_recMap1, 0, _recMap1.Length);
			Array.Clear(_recMap2, 0, _recMap2.Length);

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
					SpreadChecks();
					break;

				case "Extended":
					KeyItemChecks();
					GuardedChecks();
					SpreadChecks();
					WandererChecks();
					break;

				case "Equipment":
					KeyItemChecks();
					GuardedChecks();
					EquipmentChecks();
					break;

				case "Scenic":
					KeyItemChecks();
					GuardedChecks();
					EquipmentChecks();
					TouristChecks();
					break;

				default:
					KeyItemChecks();
					GuardedChecks();
					break;
			}

			DrawCastleProgress();
		}

		private void RelicChecks()
		{
			AddCheckpx(240, 90, 1);
			AddCheckpx(295, 40, 1);
			AddCheckpx(80, 65, 1);
			AddCheckpx(40, 60, 2);
			AddCheckpx(305, 75, 1);
			AddCheckpx(15, 175, 1);
			AddCheckpx(75, 150, 1);
			AddCheckpx(105, 95, 1);
			AddCheckpx(155, 30, 1);
			AddCheckpx(230, 15, 2);
			AddCheckpx(95, 165, 1);
			AddCheckpx(125, 145, 1);
			AddCheckpx(170, 100, 1);
			AddCheckpx(155, 40, 1);
			AddCheckpx(275, 190, 1);
			AddCheckpx(295, 75, 1);
			AddCheckpx(245, 85, 1);
			AddCheckpx(40, 195, 1);
			AddCheckpx(65, 120, 1);
			AddCheckpx(195, 20, 1);
			AddCheckpx(260, 75, 1);
			AddCheckpx(145, 205, 1);
			AddCheckpx(100, 75, 1);
			AddCheckpx(195, 200, 2);
			AddCheckpx(25, 150, 2);
			AddCheckpx(220, 185, 2);
			AddCheckpx(115, 215, 2);
			AddCheckpx(160, 65, 2);
		}


		private void KeyItemChecks()
		{
			AddCheckpx(225, 150, 1);
			AddCheckpx(40, 60, 1);
			AddCheckpx(160, 140, 1);
			AddCheckpx(205, 240, 1);
		}

		private void GuardedChecks()
		{
			AddCheckpx(85, 235, 1);
			AddCheckpx(200, 175, 1);
			AddCheckpx(115, 75, 2);
			AddCheckpx(215, 155, 2);
			AddCheckpx(250, 130, 2);
		}

		private void SpreadChecks()
		{
			AddCheckpx(65, 175, 2);
			AddCheckpx(70, 165, 2);
		}

		private void EquipmentChecks()
		{
			AddCheckpx(25, 175, 1);
			AddCheckpx(50, 190, 1);
			AddCheckpx(50, 130, 1);
			AddCheckpx(80, 140, 1);
			AddCheckpx(295, 100, 1);
			AddCheckpx(245, 90, 1);
			AddCheckpx(250, 75, 1);
			AddCheckpx(230, 90, 1);
			AddCheckpx(195, 25, 1);
			AddCheckpx(20, 110, 1);
			AddCheckpx(40, 90, 1);
			AddCheckpx(135, 35, 1);
			AddCheckpx(160, 95, 1);
			AddCheckpx(150, 60, 1);
			AddCheckpx(165, 75, 1);
			AddCheckpx(65, 105, 1);
			AddCheckpx(100, 105, 1);
			AddCheckpx(95, 85, 1);
			AddCheckpx(70, 95, 1);
			AddCheckpx(175, 120, 1);
			AddCheckpx(120, 180, 1);
			AddCheckpx(200, 195, 1);
			AddCheckpx(225, 190, 1);
			AddCheckpx(155, 225, 1);
			AddCheckpx(140, 235, 1);
			AddCheckpx(120, 235, 1);
			AddCheckpx(115, 235, 1);
			AddCheckpx(80, 155, 1);
			AddCheckpx(170, 110, 1);
			AddCheckpx(295, 120, 1);
			AddCheckpx(275, 50, 1);
			AddCheckpx(245, 55, 1);
			AddCheckpx(175, 15, 1);
			AddCheckpx(10, 120, 1);
			AddCheckpx(50, 85, 1);
			AddCheckpx(70, 45, 1);
			AddCheckpx(190, 175, 1);
			AddCheckpx(185, 190, 1);

			AddCheckpx(150, 235, 2);
			AddCheckpx(140, 235, 2);
			AddCheckpx(160, 210, 2);
			AddCheckpx(120, 210, 2);
			AddCheckpx(20, 210, 2);
			AddCheckpx(70, 195, 2);
			AddCheckpx(220, 210, 2);
			AddCheckpx(150, 175, 2);
			AddCheckpx(155, 155, 2);
			AddCheckpx(220, 165, 2);
			AddCheckpx(235, 110, 2);
			AddCheckpx(20, 130, 2);
			AddCheckpx(140, 130, 2);
			AddCheckpx(205, 80, 2);
			AddCheckpx(275, 55, 2);
			AddCheckpx(170, 45, 2);
			AddCheckpx(195, 15, 2);
			AddCheckpx(200, 15, 2);
			AddCheckpx(215, 80, 2);
			AddCheckpx(65, 175, 2);
			AddCheckpx(105, 210, 2);
			AddCheckpx(40, 200, 2);
			AddCheckpx(275, 190, 2);
			AddCheckpx(215, 145, 2);
			AddCheckpx(255, 85, 2);
			AddCheckpx(130, 105, 2);
			AddCheckpx(190, 55, 2);
			AddCheckpx(265, 60, 2);
			AddCheckpx(70, 160, 2);
			AddCheckpx(75, 160, 2);
		}

		private void TouristChecks()
		{
			AddCheckpx(300, 130, 1);
			AddCheckpx(255, 25, 1);
			AddCheckpx(125, 195, 1);
			AddCheckpx(80, 90, 1);
			AddCheckpx(95, 105, 1);

			AddCheckpx(120, 235, 2);
			AddCheckpx(250, 145, 2);
			AddCheckpx(155, 150, 2);
			AddCheckpx(85, 145, 2);
			AddCheckpx(185, 95, 2);
			AddCheckpx(135, 60, 2);
			AddCheckpx(110, 10, 2);
			AddCheckpx(305, 75, 2);
		}

		private void WandererChecks()
		{
			AddCheckpx(80, 155, 1);
			AddCheckpx(170, 110, 1);
			AddCheckpx(295, 120, 1);
			AddCheckpx(275, 50, 1);
			AddCheckpx(245, 55, 1);
			AddCheckpx(175, 15, 1);
			AddCheckpx(10, 120, 1);
			AddCheckpx(50, 85, 1);
			AddCheckpx(70, 45, 1);
			AddCheckpx(180, 175, 1);
			AddCheckpx(185, 190, 1);

			AddCheckpx(105, 210, 2);
			AddCheckpx(40, 200, 2);
			AddCheckpx(275, 190, 2);
			AddCheckpx(215, 145, 2);
			AddCheckpx(255, 85, 2);
			AddCheckpx(130, 105, 2);
			AddCheckpx(190, 55, 2);
			AddCheckpx(265, 60, 2);
			AddCheckpx(70, 160, 2);
			AddCheckpx(75, 160, 2);
		}
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
