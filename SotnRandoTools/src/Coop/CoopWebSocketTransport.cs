using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SotnRandoTools.Coop.Enums;
using SotnRandoTools.Coop.Interfaces;
using SotnRandoTools.Coop.Models;

namespace SotnRandoTools.Coop
{
	internal sealed class CoopWebSocketTransport : ICoopTransport
	{
		private const int PingInterval = 15000;
		private const int ReceiveBufferSize = 1024;
		private const int MaxRetries = 5;
		private const int ReconnectInterval = 5000;
		private const int TickInterval = 16;

		private readonly ICoopViewModel coopViewModel;
		private readonly string websocketUrl;
		private readonly string roomId;
		private readonly ConcurrentQueue<byte[]> sendQueue = new ConcurrentQueue<byte[]>();
		private readonly SemaphoreSlim sendSemaphore = new SemaphoreSlim(1, 1);

		private ClientWebSocket webSocket;
		private CancellationTokenSource cts;
		private volatile bool connected = false;
		private volatile bool manualDisconnect = false;
		private int retryCount = 0;
		private DateTime lastPong = DateTime.UtcNow;
		private int[] pings = new int[28];
		private int pingIndex = 0;

		public CoopWebSocketTransport(string websocketUrl, string roomId, ICoopViewModel coopViewModel)
		{
			this.coopViewModel = coopViewModel ?? throw new ArgumentNullException(nameof(coopViewModel));
			if (string.IsNullOrEmpty(websocketUrl)) throw new ArgumentNullException(nameof(websocketUrl));
			if (string.IsNullOrEmpty(roomId)) throw new ArgumentNullException(nameof(roomId));
			this.websocketUrl = websocketUrl;
			this.roomId = roomId;
			this.MessageQueue = new ConcurrentQueue<byte[]>();
		}

		public ConcurrentQueue<byte[]> MessageQueue { get; }

		public void Open()
		{
			if (connected)
			{
				return;
			}
			manualDisconnect = false;
			cts = new CancellationTokenSource();
			Task.Run(() => ConnectAndJoin().ConfigureAwait(false));
		}

		public void Close()
		{
			manualDisconnect = true;
			connected = false;
			coopViewModel.Status = NetworkStatus.ManuallyDisconnected;
			cts?.Cancel();
			try
			{
				if (webSocket != null && webSocket.State == WebSocketState.Open)
				{
					webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None)
						.ConfigureAwait(false).GetAwaiter().GetResult();
				}
			}
			catch
			{
			}
			webSocket?.Dispose();
			webSocket = null;
		}

		public void Send(byte[] data)
		{
			if (!connected || webSocket == null || webSocket.State != WebSocketState.Open)
			{
				return;
			}
			byte[] copy = new byte[data.Length];
			Array.Copy(data, copy, data.Length);
			sendQueue.Enqueue(copy);
			Task.Run(() => ProcessSendQueue().ConfigureAwait(false));
		}

		private async Task ConnectAndJoin()
		{
			coopViewModel.Status = NetworkStatus.Reconnecting;
			var token = cts.Token;

			while (!connected && !token.IsCancellationRequested && retryCount <= MaxRetries)
			{
				try
				{
					webSocket = new ClientWebSocket();
					await webSocket.ConnectAsync(new Uri(websocketUrl), token).ConfigureAwait(false);

					coopViewModel.Status = NetworkStatus.JoiningRoom;

					string joinMessage = JsonConvert.SerializeObject(new { roomId = this.roomId });
					byte[] joinBytes = Encoding.UTF8.GetBytes(joinMessage);
					await webSocket.SendAsync(
						new ArraySegment<byte>(joinBytes),
						WebSocketMessageType.Text,
						true,
						token).ConfigureAwait(false);

					connected = true;
					retryCount = 0;
					lastPong = DateTime.UtcNow;
					coopViewModel.Status = NetworkStatus.Connected;

					_ = Task.Run(() => ReceiveLoop(token).ConfigureAwait(false));
					_ = Task.Run(() => SendPingMessagesAsync(token).ConfigureAwait(false));
				}
				catch (OperationCanceledException)
				{
					coopViewModel.Status = NetworkStatus.ManuallyDisconnected;
					return;
				}
				catch (Exception)
				{
					retryCount++;
					if (retryCount > MaxRetries)
					{
						coopViewModel.Status = NetworkStatus.TimedOut;
						return;
					}
					coopViewModel.Status = NetworkStatus.Reconnecting;
					webSocket?.Dispose();
					webSocket = null;
					Thread.Sleep(1000);
				}
			}
		}

		private async Task ReceiveLoop(CancellationToken token)
		{
			byte[] buffer = new byte[ReceiveBufferSize];
			while (connected && !token.IsCancellationRequested)
			{
				if (webSocket == null || webSocket.State != WebSocketState.Open)
				{
					HandleDisconnect();
					return;
				}
				try
				{
					var result = await webSocket.ReceiveAsync(
						new ArraySegment<byte>(buffer),
						token).ConfigureAwait(false);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						HandleDisconnect();
						return;
					}

					if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
					{
						byte[] message = new byte[24];
						Array.Copy(buffer, message, Math.Min(result.Count, 24));
						ProcessMessage(message);
					}
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception)
				{
					HandleDisconnect();
					return;
				}
			}
		}

		private async Task ProcessSendQueue()
		{
			if (!await sendSemaphore.WaitAsync(0).ConfigureAwait(false))
			{
				return;
			}
			try
			{
				var token = cts.Token;
				while (!token.IsCancellationRequested && connected && !sendQueue.IsEmpty)
				{
					if (webSocket == null || webSocket.State != WebSocketState.Open)
					{
						return;
					}

					if (!sendQueue.TryDequeue(out byte[] data))
					{
						await Task.Delay(TickInterval).ConfigureAwait(false);
						continue;
					}

					if (data[0] == (byte) MessageType.Ping)
					{
						byte[] pingMessage = new byte[9];
						byte[] timeBytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
						pingMessage[0] = (byte) MessageType.Ping;
						for (int i = 0; i < timeBytes.Length; i++)
						{
							pingMessage[i + 1] = timeBytes[i];
						}
						data = pingMessage;
					}
					if (data[0] == (byte) MessageType.Pong)
					{
						byte[] pongMessage = new byte[9];
						byte[] timeBytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
						pongMessage[0] = (byte) MessageType.Pong;
						for (int i = 0; i < timeBytes.Length; i++)
						{
							pongMessage[i + 1] = timeBytes[i];
						}
						data = pongMessage;
					}

					await webSocket.SendAsync(
						new ArraySegment<byte>(data),
						WebSocketMessageType.Binary,
						true,
						token).ConfigureAwait(false);

					await Task.Delay(TickInterval).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception)
			{
				HandleDisconnect();
			}
			finally
			{
				sendSemaphore.Release();
				if (!sendQueue.IsEmpty && connected)
				{
					Task.Run(() => ProcessSendQueue().ConfigureAwait(false));
				}
			}
		}

		private async Task SendPingMessagesAsync(CancellationToken token)
		{
			while (!token.IsCancellationRequested && connected)
			{
				byte[] pingMessage = new byte[1];
				pingMessage[0] = (byte) MessageType.Ping;
				Send(pingMessage);

				await Task.Delay(PingInterval).ConfigureAwait(false);
			}
		}

		private void ProcessMessage(byte[] data)
		{
			MessageType msgType = (MessageType) data[0];
			if (msgType == MessageType.Pong)
			{
				lastPong = DateTime.UtcNow;
				long sentAt = BitConverter.ToInt64(data, 1);
				TimeSpan span = new TimeSpan(lastPong.Ticks - sentAt);
				SetPing(span.Milliseconds);
			}
			else if (msgType == MessageType.Ping)
			{
				long sentAt = BitConverter.ToInt64(data, 1);
				DateTime now = DateTime.UtcNow;
				TimeSpan span = new TimeSpan(now.Ticks - sentAt);
				SetPing(span.Milliseconds);
				byte[] pongMessage = new byte[1];
				pongMessage[0] = (byte) MessageType.Pong;
				Send(pongMessage);
			}
			else
			{
				MessageQueue.Enqueue(data);
			}
		}

		private void SetPing(int milliseconds)
		{
			pings[pingIndex] = milliseconds;
			pingIndex = (pingIndex + 1) % pings.Length;
			int totalMs = 0;
			for (int i = 0; i < pings.Length; i++)
			{
				totalMs += pings[i];
			}
			int ping = (totalMs / pings.Length) - TickInterval;
			if (ping < 0)
			{
				ping = 0;
			}
			coopViewModel.Ping = ping;
		}

		private void HandleDisconnect()
		{
			if (!connected)
			{
				return;
			}
			connected = false;
			webSocket?.Dispose();
			webSocket = null;

			if (!manualDisconnect)
			{
				coopViewModel.Status = NetworkStatus.Reconnecting;
				Task.Run(() => AutoReconnect().ConfigureAwait(false));
			}
			else
			{
				coopViewModel.Status = NetworkStatus.Disconnected;
			}
		}

		private async Task AutoReconnect()
		{
			while (!manualDisconnect)
			{
				await Task.Delay(ReconnectInterval).ConfigureAwait(false);
				if (manualDisconnect)
				{
					return;
				}

				try
				{
					cts?.Cancel();
					cts?.Dispose();
					cts = new CancellationTokenSource();
					var token = cts.Token;

					webSocket = new ClientWebSocket();
					await webSocket.ConnectAsync(new Uri(websocketUrl), token).ConfigureAwait(false);

					coopViewModel.Status = NetworkStatus.JoiningRoom;

					string joinMessage = JsonConvert.SerializeObject(new { roomId = this.roomId });
					byte[] joinBytes = Encoding.UTF8.GetBytes(joinMessage);
					await webSocket.SendAsync(
						new ArraySegment<byte>(joinBytes),
						WebSocketMessageType.Text,
						true,
						token).ConfigureAwait(false);

					connected = true;
					retryCount = 0;
					lastPong = DateTime.UtcNow;
					coopViewModel.Status = NetworkStatus.Connected;

					_ = Task.Run(() => ReceiveLoop(token).ConfigureAwait(false));
					_ = Task.Run(() => SendPingMessagesAsync(token).ConfigureAwait(false));
					return;
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception)
				{
					coopViewModel.Status = NetworkStatus.Reconnecting;
					webSocket?.Dispose();
					webSocket = null;
				}
			}
		}
	}
}
