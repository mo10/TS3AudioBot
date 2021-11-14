// TSLib - A free TeamSpeak 3 and 5 client library
// Copyright (C) 2017  TSLib contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.Linq;
using TSLib.Commands;
using TSLib.Helper;

namespace TSLib.Full;

// TODO include udp/ip header size to get correct values
/// <summary>Provides connection stats by logging packets.</summary>
public sealed class NetworkStats
{
	private readonly long[] outPackets = new long[3];
	private readonly long[] inPackets = new long[3];
	private readonly long[] outBytes = new long[3];
	private readonly long[] inBytes = new long[3];
	private readonly Queue<PacketData> outBytesTime = new();
	private readonly Queue<PacketData> inBytesTime = new();
	private readonly Queue<TimeSpan> pingTimes = new(60);
	private static readonly TimeSpan TimeSecond = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan TimeMinute = TimeSpan.FromMinutes(1);
	private readonly object queueLock = new();

	internal void LogOutPacket<TDir>(ref Packet<TDir> packet)
	{
		var kind = TypeToKind(packet.PacketType);
		lock (queueLock)
		{
			outPackets[(int)kind]++;
			outBytes[(int)kind] += packet.Raw.Length;
			DropOver(outBytesTime, TimeMinute);
			outBytesTime.Enqueue(new PacketData((ushort)packet.Raw.Length, Tools.Now, kind));
		}
	}

	internal void LogInPacket<TDir>(ref Packet<TDir> packet)
	{
		var kind = TypeToKind(packet.PacketType);
		lock (queueLock)
		{
			inPackets[(int)kind]++;
			inBytes[(int)kind] += packet.Raw.Length;
			DropOver(inBytesTime, TimeMinute);
			inBytesTime.Enqueue(new PacketData((ushort)packet.Raw.Length, Tools.Now, kind));
		}
	}

	public static void LogLostPings(int count)
	{
		// TODO
	}

	public void AddPing(TimeSpan ping)
	{
		lock (queueLock)
		{
			if (pingTimes.Count >= 60)
				pingTimes.Dequeue();
			pingTimes.Enqueue(ping);
		}
	}

	private static PacketKind TypeToKind(PacketType type)
	{
		return type switch
		{
			PacketType.Voice or PacketType.VoiceWhisper => PacketKind.Speech,
			PacketType.Command or PacketType.CommandLow or PacketType.Ack or PacketType.AckLow or PacketType.Init1 => PacketKind.Control,
			PacketType.Ping or PacketType.Pong => PacketKind.Keepalive,
			var _unhandled => throw Tools.UnhandledDefault(_unhandled),
		};
	}

	private static void GetWithin(Queue<PacketData> queue, TimeSpan time, out DataCatergory data)
	{
		var now = Tools.Now;
		var nowThresh = now - time;
		data = new DataCatergory();
		foreach (var pack in queue.Reverse())
			if (nowThresh <= pack.SendPoint)
			{
				switch (pack.Kind)
				{
				case PacketKind.Speech: data.Speech += pack.Size; break;
				case PacketKind.Keepalive: data.Keepalive += pack.Size; break;
				case PacketKind.Control: data.Control += pack.Size; break;
				case var _unhandled: throw Tools.UnhandledDefault(_unhandled);
				}
			}
			else { break; }
		data.Speech = (long)(data.Speech / time.TotalSeconds);
		data.Keepalive = (long)(data.Keepalive / time.TotalSeconds);
		data.Control = (long)(data.Control / time.TotalSeconds);
	}

	private static void DropOver(Queue<PacketData> queue, TimeSpan time)
	{
		var now = Tools.Now;
		while (queue.Count > 0 && now - queue.Peek().SendPoint > time)
			queue.Dequeue();
	}

	public TsCommand GenerateStatusAnswer()
	{
		DataCatergory lastSecondIn;
		DataCatergory lastSecondOut;
		DataCatergory lastMinuteIn;
		DataCatergory lastMinuteOut;
		double lastPing;
		double deviationPing;
		lock (queueLock)
		{
			GetWithin(inBytesTime, TimeSecond, out lastSecondIn);
			GetWithin(outBytesTime, TimeSecond, out lastSecondOut);
			GetWithin(inBytesTime, TimeMinute, out lastMinuteIn);
			GetWithin(outBytesTime, TimeMinute, out lastMinuteOut);
			if (pingTimes.Count > 0)
			{
				lastPing = pingTimes.Last().TotalMilliseconds;
				deviationPing = StdDev(pingTimes.Select(ts => ts.TotalMilliseconds));
			}
			else
			{
				lastPing = deviationPing = 0;
			}
		}

		return new TsCommand("setconnectioninfo") {
				{ "connection_ping", Math.Round(lastPing, 0) },
				{ "connection_ping_deviation", deviationPing },
				{ "connection_packets_sent_speech", outPackets[(int)PacketKind.Speech] },
				{ "connection_packets_sent_keepalive", outPackets[(int)PacketKind.Keepalive] },
				{ "connection_packets_sent_control", outPackets[(int)PacketKind.Control] },
				{ "connection_bytes_sent_speech", outBytes[(int)PacketKind.Speech] },
				{ "connection_bytes_sent_keepalive", outBytes[(int)PacketKind.Keepalive] },
				{ "connection_bytes_sent_control", outBytes[(int)PacketKind.Control] },
				{ "connection_packets_received_speech", inPackets[(int)PacketKind.Speech] },
				{ "connection_packets_received_keepalive", inPackets[(int)PacketKind.Keepalive] },
				{ "connection_packets_received_control", inPackets[(int)PacketKind.Control] },
				{ "connection_bytes_received_speech", inBytes[(int)PacketKind.Speech] },
				{ "connection_bytes_received_keepalive", inBytes[(int)PacketKind.Keepalive] },
				{ "connection_bytes_received_control", inBytes[(int)PacketKind.Control] },
				{ "connection_server2client_packetloss_speech", 42.0000f },
				{ "connection_server2client_packetloss_keepalive", 1.0000f },
				{ "connection_server2client_packetloss_control", 0.5000f },
				{ "connection_server2client_packetloss_total", 0.0000f },
				{ "connection_bandwidth_sent_last_second_speech", lastSecondOut.Speech },
				{ "connection_bandwidth_sent_last_second_keepalive", lastSecondOut.Keepalive },
				{ "connection_bandwidth_sent_last_second_control", lastSecondOut.Control },
				{ "connection_bandwidth_sent_last_minute_speech", lastMinuteOut.Speech },
				{ "connection_bandwidth_sent_last_minute_keepalive", lastMinuteOut.Keepalive },
				{ "connection_bandwidth_sent_last_minute_control", lastMinuteOut.Control },
				{ "connection_bandwidth_received_last_second_speech", lastSecondIn.Speech },
				{ "connection_bandwidth_received_last_second_keepalive", lastSecondIn.Keepalive },
				{ "connection_bandwidth_received_last_second_control", lastSecondIn.Control },
				{ "connection_bandwidth_received_last_minute_speech", lastMinuteIn.Speech },
				{ "connection_bandwidth_received_last_minute_keepalive", lastMinuteIn.Keepalive },
				{ "connection_bandwidth_received_last_minute_control", lastMinuteIn.Control },
			};
	}

	private static double StdDev(IEnumerable<double> values)
	{
		double avg = values.Average();
		double sum = 0;
		int n = 0;
		foreach (double val in values)
		{
			n++;
			sum += (val - avg) * (val - avg);
		}
		if (n > 1)
			return Math.Sqrt(sum / (n - 1));
		return 0;
	}

	public void Reset()
	{
		Array.Clear(outPackets, 0, outPackets.Length);
		Array.Clear(inPackets, 0, inPackets.Length);
		Array.Clear(outBytes, 0, outBytes.Length);
		Array.Clear(inBytes, 0, inBytes.Length);
		lock (queueLock)
		{
			outBytesTime.Clear();
			inBytesTime.Clear();
			pingTimes.Clear();
		}
	}

	private enum PacketKind : ushort
	{
		Speech,
		Keepalive,
		Control,
	}

	private record struct PacketData(ushort Size, DateTime SendPoint, PacketKind Kind);

	struct DataCatergory
	{
		public long Speech { get; set; }
		public long Keepalive { get; set; }
		public long Control { get; set; }
	}
}
