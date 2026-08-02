using System;
using SotnRandoTools.Coop.Models;

namespace SotnRandoTools.Coop.Interfaces
{
	internal interface ICoopController
	{
		CoopState CoopState { get; }
		bool SynchRequested { get; set; }

		event Action<byte, ushort, ushort>? OnPlayerLocationUpdated;
		event Action<byte, ushort, ushort>? OnPlayerHistoryUpdated;

		void Connect(string hostIp, int port);
		void ConnectOnline(string websocketUrl, string roomId);
		void Disconnect();
		void StartServer(int port);
		void StopServer();
		void DisposeAll();
		void SendData(byte[] data);
		bool IsConnected();
		// --- New Internal Network Routing Handlers ---
		void UpdatePlayerLocation(byte playerId, ushort x, ushort y);
		void UpdatePlayerHistory(byte castleNum, ushort tileX, ushort tileY);
	}
}