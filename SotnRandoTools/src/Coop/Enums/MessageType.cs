using System;

namespace SotnRandoTools.Coop.Enums
{
	[Flags]
	public enum MessageType : byte
	{
		Ping,
		Pong,
		Relic,
		Item,
		WarpFirstCastle,
		WarpSecondCastle,
		Shortcut,
		SynchRequest,
		SynchAll,
		Location,
		// --- New Flags Added Below ---
		PlayerCoords, // Live X, Y map coordinates
		RoomHistory,   // List or flags of explored rooms

		BossDefeat
	}
}
