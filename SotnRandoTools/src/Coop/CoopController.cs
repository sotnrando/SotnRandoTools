using System;
using System.Net;
using SotnApi.Interfaces;
using SotnRandoTools.Configuration.Interfaces;
using SotnRandoTools.Constants;
using SotnRandoTools.Coop.Enums;
using SotnRandoTools.Coop.Interfaces;
using SotnRandoTools.Coop.Models;
using SotnRandoTools.RandoTracker.Interfaces;
using SotnRandoTools.Services;

namespace SotnRandoTools.Coop
{
	internal sealed class CoopController : ICoopController
	{
		private readonly CoopReceiver coopReceiver;
		private readonly CoopSender coopSender;
		private readonly ICoopViewModel coopViewModel;
		private readonly ISotnApi sotnApi;
		private ICoopTransport? transport;
		private CoopState coopState;

		public CoopController(IToolConfig toolConfig, ISotnApi sotnApi, ICoopViewModel coopViewModel, INotificationService notificationService, ILocationTracker locationTracker)
		{
			this.coopViewModel = coopViewModel ?? throw new ArgumentNullException(nameof(coopViewModel));
			this.sotnApi = sotnApi ?? throw new ArgumentNullException(nameof(sotnApi));
			coopState = new CoopState(sotnApi, locationTracker);
			coopSender = new CoopSender(toolConfig, sotnApi, notificationService, this);
			coopReceiver = new CoopReceiver(toolConfig, sotnApi, notificationService, this);
		}

		public CoopState CoopState
		{
			get
			{
				return coopState;
			}
		}

		public bool SynchRequested { get; set; }

		public void Update()
		{
			if (transport == null)
			{
				return;
			}
			if (sotnApi.GameApi.InAlucardMode())
			{
				coopState.Update();
			}
			coopSender.Update();
			coopReceiver.Update();
		}

		public void Connect(string hostIp, int port)
		{
			if (port < Globals.PortMinimum || port > Globals.PortMaximum) throw new ArgumentOutOfRangeException($"Port must be between {Globals.PortMinimum} and {Globals.PortMaximum}");
			if (string.IsNullOrEmpty(hostIp)) throw new ArgumentNullException(nameof(hostIp));
			if (!IPAddress.TryParse(hostIp, out var ip)) throw new ArgumentException("Invalid Ip string.");

			if (transport is null || !(transport is CoopNetworking))
			{
				transport = new CoopNetworking(IPAddress.Parse(hostIp), port, coopViewModel);
				coopReceiver.MessageQueue = transport.MessageQueue;
			}
			else
			{
				var networking = (CoopNetworking) transport;
				networking.RemoteServerIp = IPAddress.Parse(hostIp);
				networking.RemoteServerPort = port;
				coopReceiver.MessageQueue = transport.MessageQueue;
			}

			transport.Open();

			return;
		}

		public void ConnectOnline(string websocketUrl, string roomId)
		{
			if (string.IsNullOrEmpty(websocketUrl)) throw new ArgumentNullException(nameof(websocketUrl));
			if (string.IsNullOrEmpty(roomId)) throw new ArgumentNullException(nameof(roomId));

			DisposeAll();
			transport = new CoopWebSocketTransport(websocketUrl, roomId, coopViewModel);
			coopReceiver.MessageQueue = transport.MessageQueue;
			transport.Open();
		}

		public void Disconnect()
		{
			if (transport is not null)
			{
				transport.Close();
			}
		}

		public void StartServer(int port)
		{
			if (port < Globals.PortMinimum || port > Globals.PortMaximum) throw new ArgumentOutOfRangeException($"Port must be between {Globals.PortMinimum} and {Globals.PortMaximum}");
			string hostName = Dns.GetHostName();

			if (transport is null || !(transport is CoopNetworking))
			{
				transport = new CoopNetworking(port, coopViewModel);
				coopReceiver.MessageQueue = transport.MessageQueue;
			}

			transport.Open();
			return;
		}

		public void StopServer()
		{
			if (transport is not null)
			{
				transport.Close();
			}
		}

		public void DisposeAll()
		{
			if (transport is not null)
			{
				transport.Close();
				transport = null;
			}
		}

		public void SendData(byte[] data)
		{
			System.Diagnostics.Debug.Assert(data != null);
			System.Diagnostics.Debug.Assert(data.Length >= 1);

			if (transport is not null)
			{
				transport.Send(data);
			}
			else
			{
				Console.WriteLine("No connection!");
			}
		}

		public bool IsConnected()
		{
			if (transport is not null && (coopViewModel.Status == NetworkStatus.Connected || coopViewModel.Status == NetworkStatus.ClientConnected))
			{
				return true;
			}
			else
			{
				return false;
			}
		}
	}
}
