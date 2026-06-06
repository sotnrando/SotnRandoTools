using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using BizHawk.Client.Common;
using SotnApi.Interfaces;
using SotnRandoTools.Configuration.Interfaces;
using SotnRandoTools.Constants;
using SotnRandoTools.Coop;
using SotnRandoTools.Coop.Enums;
using SotnRandoTools.Coop.Models;
using SotnRandoTools.RandoTracker.Interfaces;
using SotnRandoTools.Services;

namespace SotnRandoTools
{
	internal sealed partial class CoopForm : Form
	{
		private readonly IToolConfig toolConfig;
		private CoopController? coopController;
		private readonly INotificationService notificationService;
		private CoopViewModel coopViewModel = new CoopViewModel();
		private bool addressValidated = false;
		private bool onlineConnected = false;
		private Color BaseBackground = Color.FromArgb(17, 0, 17);

		public CoopForm(IToolConfig toolConfig, ISotnApi sotnApi, IJoypadApi joypadApi, INotificationService notificationService, ILocationTracker locationTracker)
		{
			if (toolConfig is null) throw new ArgumentNullException(nameof(toolConfig));
			if (sotnApi is null) throw new ArgumentNullException(nameof(sotnApi));
			if (joypadApi is null) throw new ArgumentNullException(nameof(joypadApi));
			if (notificationService is null) throw new ArgumentNullException(nameof(notificationService));
			this.toolConfig = toolConfig;
			this.notificationService = notificationService;

			this.coopController = new CoopController(toolConfig, sotnApi, coopViewModel, notificationService, locationTracker);
			InitializeComponent();
			SuspendLayout();
			ResumeLayout();
			this.portNumeric.Controls[0].Visible = false;
			coopViewModel.PropertyChanged += CoopViewModelPropertyChanged;
		}

		private void CoopViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(CoopViewModel.Ping):
					this.ping.Text = coopViewModel.Ping.ToString();
					break;
				case nameof(CoopViewModel.Status):
					SetServerStatus(coopViewModel.Status);
					break;
				default:
					break;
			}
		}

		private void SetServerStatus(NetworkStatus status)
		{
			switch (status)
			{
				case NetworkStatus.Started:
					notificationService.AddMessage("Server started");
					this.hostButton.Text = "Stop";
					this.hostButton.ForeColor = Color.Black;
					this.hostButton.BackColor = Color.Aqua;
					this.connectButton.BackColor = BaseBackground;
					this.connectButton.ForeColor = Color.White;
					this.connectButton.Enabled = false;
					this.targetIp.Enabled = false;
					this.portNumeric.Enabled = false;
					this.IPlabel.Enabled = false;
					SetOnlineGroupEnabled(false);
					break;
				case NetworkStatus.Stopped:
					notificationService.AddMessage("Server stopped");
					this.hostButton.Text = "Host";
					this.hostButton.BackColor = BaseBackground;
					this.hostButton.ForeColor = Color.White;
					this.connectButton.Enabled = true;
					this.targetIp.Enabled = true;
					this.portNumeric.Enabled = true;
					this.IPlabel.Enabled = true;
					SetOnlineGroupEnabled(true);
					break;
				case NetworkStatus.ServerError:
					notificationService.AddMessage("Server error");
					this.hostButton.BackColor = Color.Crimson;
					this.hostButton.Text = "Host";
					this.hostButton.ForeColor = Color.White;
					this.connectButton.Enabled = true;
					this.targetIp.Enabled = true;
					this.portNumeric.Enabled = true;
					this.IPlabel.Enabled = true;
					SetOnlineGroupEnabled(true);
					break;
				case NetworkStatus.ClientError:
					notificationService.AddMessage("Client error");
					this.connectButton.BackColor = Color.Crimson;
					this.connectButton.Text = "Connect";
					this.connectButton.ForeColor = Color.White;
					this.hostButton.Enabled = true;
					this.hostButton.Enabled = true;
					this.targetIp.Enabled = true;
					this.portNumeric.Enabled = true;
					SetOnlineGroupEnabled(true);
					break;
				case NetworkStatus.ClientConnected:
					notificationService.AddMessage("Client connected");
					this.hostButton.BackColor = Color.SpringGreen;
					break;
				case NetworkStatus.ClientDisconnected:
					notificationService.AddMessage("Client disconnected");
					this.hostButton.BackColor = Color.Crimson;
					break;
				case NetworkStatus.Connected:
					notificationService.AddMessage("Connected");
					if (onlineConnected)
					{
						this.onlineConnectButton.Text = "Disconnect";
						this.onlineConnectButton.ForeColor = Color.Black;
						this.onlineConnectButton.BackColor = Color.SpringGreen;
						this.onlineConnectButton.Enabled = true;
						this.roomIdTextBox.Enabled = false;
						this.hostButton.Enabled = false;
						this.connectButton.Enabled = false;
						this.targetIp.Enabled = false;
						this.portNumeric.Enabled = false;
					}
					else
					{
						this.connectButton.Text = "Disconnect";
						this.connectButton.ForeColor = Color.Black;
						this.connectButton.BackColor = Color.SpringGreen;
						this.hostButton.BackColor = BaseBackground;
						this.hostButton.ForeColor = Color.White;
						this.connectButton.Enabled = true;
						this.hostButton.Enabled = false;
						this.targetIp.Enabled = false;
						this.portNumeric.Enabled = false;
						SetOnlineGroupEnabled(false);
					}
					break;
				case NetworkStatus.JoiningRoom:
					notificationService.AddMessage("Joining room...");
					this.onlineConnectButton.Text = "Joining...";
					this.onlineConnectButton.BackColor = Color.Coral;
					this.onlineConnectButton.Enabled = true;
					this.roomIdTextBox.Enabled = false;
					this.hostButton.Enabled = false;
					this.connectButton.Enabled = false;
					this.targetIp.Enabled = false;
					this.portNumeric.Enabled = false;
					break;
				case NetworkStatus.Reconnecting:
					notificationService.AddMessage("Reconnecting");
					if (onlineConnected)
					{
						this.onlineConnectButton.Text = "Reconnecting";
						this.onlineConnectButton.BackColor = Color.Coral;
						this.onlineConnectButton.Enabled = true;
						this.roomIdTextBox.Enabled = false;
						this.hostButton.Enabled = false;
						this.connectButton.Enabled = false;
						this.targetIp.Enabled = false;
						this.portNumeric.Enabled = false;
					}
					else
					{
						this.connectButton.Text = "Reconnecting";
						this.connectButton.BackColor = Color.Coral;
						this.hostButton.BackColor = BaseBackground;
						this.hostButton.ForeColor = Color.White;
						this.connectButton.Enabled = true;
						this.hostButton.Enabled = false;
						this.targetIp.Enabled = false;
						this.portNumeric.Enabled = false;
						SetOnlineGroupEnabled(false);
					}
					break;
				case NetworkStatus.Disconnected:
					notificationService.AddMessage("Disconnected");
					ResetAllControls();
					break;
				case NetworkStatus.TimedOut:
					notificationService.AddMessage("Timed out");
					ResetAllControls();
					if (onlineConnected)
					{
						this.onlineConnectButton.BackColor = Color.Crimson;
					}
					else
					{
						this.connectButton.BackColor = Color.Crimson;
						this.connectButton.ForeColor = Color.White;
					}
					onlineConnected = false;
					break;
				case NetworkStatus.ManuallyDisconnected:
					notificationService.AddMessage("Disconnected");
					ResetAllControls();
					onlineConnected = false;
					break;
				default: break;
			}
		}

		private void ResetAllControls()
		{
			this.connectButton.Text = "Connect";
			this.connectButton.BackColor = BaseBackground;
			this.connectButton.ForeColor = Color.White;
			this.connectButton.Enabled = true;
			this.hostButton.Enabled = true;
			this.hostButton.BackColor = BaseBackground;
			this.hostButton.ForeColor = Color.White;
			this.targetIp.Enabled = true;
			this.portNumeric.Enabled = true;
			this.onlineConnectButton.Text = "Connect";
			this.onlineConnectButton.BackColor = BaseBackground;
			this.onlineConnectButton.ForeColor = Color.White;
			this.onlineConnectButton.Enabled = true;
			this.roomIdTextBox.Enabled = true;
		}

		private void SetOnlineGroupEnabled(bool enabled)
		{
			this.onlineConnectButton.Enabled = enabled;
			this.roomIdTextBox.Enabled = enabled;
			if (enabled)
			{
				this.onlineConnectButton.BackColor = BaseBackground;
				this.onlineConnectButton.ForeColor = Color.White;
			}
		}

		public void UpdateCoop()
		{
			if (coopController != null)
			{
				coopController.Update();
			}
		}

		private void CoopForm_Load(object sender, EventArgs e)
		{
			if (SystemInformation.VirtualScreen.Width > toolConfig.Coop.Location.X && SystemInformation.VirtualScreen.Height > toolConfig.Coop.Location.Y)
			{
				this.Location = toolConfig.Coop.Location;
			}
			this.portNumeric.Value = toolConfig.Coop.DefaultPort;
			this.targetIp.Text = "127.0.0.1:46318";
			ValidateAddress();
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				this.Icon = new Icon(Paths.BizAlucardIcon);
			}
		}

		private void CoopForm_Move(object sender, EventArgs e)
		{
			if (this.Location.X > 0)
			{
				toolConfig.Coop.Location = this.Location;
			}
		}

		private void hostButton_Click(object sender, EventArgs e)
		{
			if (coopViewModel.Status != NetworkStatus.Stopped && coopViewModel.Status != NetworkStatus.ServerError)
			{
				coopController.StopServer();
				return;
			}

			int port = (int) this.portNumeric.Value;

			if (port > 1024 && port < 49151)
			{
				coopController.StartServer(port);
			}
		}

		private void connectButton_Click(object sender, EventArgs e)
		{
			if (coopViewModel.Status == NetworkStatus.Connected)
			{
				coopController.Disconnect();
				return;
			}

			string[] hostAddress = this.targetIp.Text.Split(':');

			if (!addressValidated)
			{
				Console.WriteLine("Invalid address!");
			}
			else
			{
				onlineConnected = false;
				coopController.Connect(hostAddress[0], Int32.Parse(hostAddress[1]));
			}
		}

		private void onlineConnectButton_Click(object sender, EventArgs e)
		{
			if (onlineConnected && coopViewModel.Status == NetworkStatus.Connected)
			{
				coopController.Disconnect();
				onlineConnected = false;
				return;
			}

			string roomId = this.roomIdTextBox.Text.Trim();
			if (string.IsNullOrEmpty(roomId))
			{
				this.addressTooltip.SetToolTip(roomIdTextBox, "Room ID cannot be empty!");
				this.addressTooltip.ToolTipIcon = ToolTipIcon.Warning;
				this.addressTooltip.Active = true;
				return;
			}

			string websocketUrl = toolConfig.Coop.WebSocketUrl;
			if (string.IsNullOrEmpty(websocketUrl))
			{
				this.addressTooltip.SetToolTip(roomIdTextBox, "WebSocket URL not configured! Set it in Co-op Settings.");
				this.addressTooltip.ToolTipIcon = ToolTipIcon.Warning;
				this.addressTooltip.Active = true;
				return;
			}

			onlineConnected = true;
			coopController.ConnectOnline(websocketUrl, roomId);
		}

		private void targetIp_Validating(object sender, System.ComponentModel.CancelEventArgs e)
		{
			//If validation is triggered by the form, return in order to ignore it and let the user close the window.
			if (this.ActiveControl.Equals(sender))
				return;
			if (!ValidateAddress())
			{
				e.Cancel = true;
			}
		}

		private bool ValidateAddress()
		{
			Regex validIpPort = new Regex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3}).(\d{1,3})\:(\d{3,5})$");
			if (!validIpPort.IsMatch(this.targetIp.Text))
			{
				addressValidated = false;
				this.connectButton.BackColor = Color.Red;
				//this.targetIp.BackColor = Color.Red;
				this.addressTooltip.SetToolTip(targetIp, "Invalid address!");
				this.addressTooltip.ToolTipIcon = ToolTipIcon.Warning;
				this.addressTooltip.Active = true;
				return false;
			}
			addressValidated = true;
			return true;
		}

		private void CoopForm_FormClosing(object sender, FormClosingEventArgs e)
		{
			coopController.DisposeAll();
			coopController = null;
			onlineConnected = false;
		}

		private void targetIp_TextChanged(object sender, EventArgs e)
		{
			this.targetIp.BackColor = Color.White;
		}

		private void targetIp_Validated(object sender, EventArgs e)
		{
			addressValidated = true;
			this.targetIp.BackColor = Color.White;
			this.addressTooltip.Active = false;
		}
	}
}
