using System.Collections.Concurrent;

namespace SotnRandoTools.Coop.Interfaces
{
	internal interface ICoopTransport
	{
		ConcurrentQueue<byte[]> MessageQueue { get; }
		void Send(byte[] data);
		void Open();
		void Close();
	}
}
