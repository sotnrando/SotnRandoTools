using System.Numerics;

namespace SotnRandoTools.RandoTracker.Models
{
	internal struct Item
	{
		public ushort CollectedAt;
		public byte Value;
		public byte Index;
		public byte X;
		public byte Y;
		public bool Status;
		public bool Collected;
		public bool Equipped;

		public Vector2 Position { get; internal set; }
	}
}
