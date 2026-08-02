using System;
using System.Windows.Forms;
using SotnRandoTools.Configuration.Interfaces;
using SotnRandoTools.Services;

namespace SotnRandoTools
{
	public partial class CoopSettingsPanel : UserControl
	{
		private readonly IToolConfig? toolConfig;

		public CoopSettingsPanel(IToolConfig toolConfig)
		{
			if (toolConfig is null) throw new ArgumentNullException(nameof(toolConfig));
			this.toolConfig = toolConfig;
			InitializeComponent();
		}

		internal INotificationService NotificationService { get; set; }

		private void MultiplayerSettingsPanel_Load(object sender, EventArgs e)
		{
			portTextBox.Text = toolConfig.Coop.DefaultPort.ToString();
			volumeBar.Value = toolConfig.Coop.Volume;
			sendComboBox.SelectedIndex = toolConfig.Coop.SendButton;
			webSocketUrlTextBox.Text = toolConfig.Coop.WebSocketUrl ?? "";

			// Load the saved toggle state when the panel opens
			sendBossDefeatCheckBox.Checked = toolConfig.Coop.SendBossDefeat;
		}

		private void saveButton_Click(object sender, EventArgs e)
		{
			toolConfig.SaveConfig();
		}

		private void portTextBox_TextChanged(object sender, EventArgs e)
		{
			toolConfig.Coop.DefaultPort = Int32.Parse(portTextBox.Text);
		}

		private void volumeBar_Scroll(object sender, EventArgs e)
		{
			toolConfig.Coop.Volume = volumeBar.Value;
			if (NotificationService is not null)
			{
				NotificationService.Volume = (float) volumeBar.Value / 10F;
			}
		}

		private void sendComboBox_SelectedIndexChanged(object sender, EventArgs e)
		{
			toolConfig.Coop.SendButton = sendComboBox.SelectedIndex;
		}

		private void webSocketUrlTextBox_TextChanged(object sender, EventArgs e)
		{
			toolConfig.Coop.WebSocketUrl = webSocketUrlTextBox.Text;
		}

		// Update the configuration whenever the user changes the checkbox state
		private void sendBossDefeatCheckBox_CheckedChanged(object sender, EventArgs e)
		{
			toolConfig.Coop.SendBossDefeat = sendBossDefeatCheckBox.Checked;
		}
	}
}