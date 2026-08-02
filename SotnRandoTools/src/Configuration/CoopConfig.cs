using System.Drawing;

namespace SotnRandoTools.Configuration
{
	public class CoopConfig
	{
		public CoopConfig()
		{
			DefaultPort = 46318;
			Volume = 5;
			SendButton = 0;
			WebSocketUrl = "wss://api.sotn.io/coop";
		}

		public Point Location { get; set; }
		public int SendButton { get; set; }
		public int DefaultPort { get; set; }
		public int Volume { get; set; }
		public string WebSocketUrl { get; set; }
		public bool SendBossDefeat { get; set; }
	}
}
