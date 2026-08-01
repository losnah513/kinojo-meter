using PacketDotNet;
using SharpPcap;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace KinojoMeterPrototype
{
    internal sealed class CapturedTcpPayloadEventArgs : EventArgs
    {
        public byte[] Payload { get; private set; }
        public DateTime TimestampUtc { get; private set; }
        public string FlowKey { get; private set; }
        public string ConnectionKey { get; private set; }
        public string Direction { get; private set; }
        public uint SequenceNumber { get; private set; }
        public CapturedTcpPayloadEventArgs(byte[] payload, DateTime timestampUtc, string sourceEndpoint, string destinationEndpoint, uint sequenceNumber)
        {
            Payload = payload ?? new byte[0];
            TimestampUtc = timestampUtc;
            var source = sourceEndpoint ?? "";
            var destination = destinationEndpoint ?? "";
            FlowKey = source + ">" + destination;
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(source, destination);
            ConnectionKey = comparison <= 0 ? source + "<>" + destination : destination + "<>" + source;
            Direction = comparison <= 0 ? "A_TO_B" : "B_TO_A";
            SequenceNumber = sequenceNumber;
        }
    }

    internal sealed class GameFrameEventArgs : EventArgs
    {
        public byte[] Frame { get; private set; }
        public DateTime TimestampUtc { get; private set; }
        public string FlowKey { get; private set; }
        public string ConnectionKey { get; private set; }
        public string Direction { get; private set; }
        public GameFrameEventArgs(byte[] frame, DateTime timestampUtc, string flowKey, string connectionKey, string direction)
        {
            Frame = frame ?? new byte[0];
            TimestampUtc = timestampUtc;
            FlowKey = flowKey ?? "";
            ConnectionKey = connectionKey ?? "";
            Direction = direction ?? "";
        }
    }

    internal interface INetworkCaptureService : IDisposable
    {
        string EngineName { get; }
        bool IsRunning { get; }
        event EventHandler<CapturedTcpPayloadEventArgs> PayloadReceived;
        event EventHandler<string> StatusChanged;
        void Start();
        void Stop();
    }

    internal sealed class NpcapCaptureService : INetworkCaptureService
    {
        private readonly List<ICaptureDevice> _devices = new List<ICaptureDevice>();
        public string EngineName { get { return "NPCAP"; } }
        public bool IsRunning { get; private set; }
        public event EventHandler<CapturedTcpPayloadEventArgs> PayloadReceived;
        public event EventHandler<string> StatusChanged;

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                var candidates = CaptureDeviceList.Instance.Cast<ICaptureDevice>().ToList();
                if (candidates.Count == 0) throw new InvalidOperationException("Npcap 캡처 장치를 찾지 못했습니다.");
                foreach (var device in candidates)
                {
                    try
                    {
                        device.OnPacketArrival += OnPacketArrival;
                        device.Open(DeviceModes.Promiscuous, 1000);
                        device.Filter = "tcp and greater 54 and not (port 80 or port 443 or port 8080 or port 8443)";
                        device.StartCapture();
                        _devices.Add(device);
                    }
                    catch
                    {
                        try { device.OnPacketArrival -= OnPacketArrival; device.Close(); } catch { }
                    }
                }
                if (_devices.Count == 0) throw new InvalidOperationException("Npcap 장치를 열지 못했습니다. Npcap 설치와 관리자 권한을 확인하세요.");
                IsRunning = true;
                RaiseStatus("NPCAP · 전체 네트워크 어댑터 감시 중");
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private void OnPacketArrival(object sender, PacketCapture capture)
        {
            try
            {
                var raw = capture.GetPacket();
                var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                var ip = packet.Extract<IPPacket>();
                var tcp = packet.Extract<TcpPacket>();
                if (ip == null || tcp == null || tcp.PayloadData == null || tcp.PayloadData.Length == 0) return;
                var source = BuildEndpoint(ip.SourceAddress, tcp.SourcePort);
                var destination = BuildEndpoint(ip.DestinationAddress, tcp.DestinationPort);
                PayloadReceived?.Invoke(this, new CapturedTcpPayloadEventArgs(tcp.PayloadData, raw.Timeval.Date.ToUniversalTime(), source, destination, tcp.SequenceNumber));
            }
            catch { }
        }

        public void Stop()
        {
            foreach (var device in _devices.ToArray())
            {
                try { device.StopCapture(); } catch { }
                try { device.OnPacketArrival -= OnPacketArrival; } catch { }
                try { device.Close(); } catch { }
            }
            _devices.Clear(); IsRunning = false;
        }

        private static string BuildEndpoint(IPAddress address, ushort port)
        {
            return address + ":" + port;
        }
        private void RaiseStatus(string text) { StatusChanged?.Invoke(this, text); }
        public void Dispose() { Stop(); }
    }

    internal sealed class WinDivertCaptureService : INetworkCaptureService
    {
        private const uint LayerNetwork = 0;
        private const uint ShutdownReceive = 0x1;
        private const uint AddressBufferSize = 80;
        private const string Filter = "tcp and tcp.PayloadLength > 0 and not (tcp.DstPort == 80 or tcp.SrcPort == 80 or tcp.DstPort == 443 or tcp.SrcPort == 443 or tcp.DstPort == 8080 or tcp.SrcPort == 8080 or tcp.DstPort == 8443 or tcp.SrcPort == 8443)";
        private readonly object _gate = new object();
        private IntPtr _handle;
        private IntPtr _address;
        private bool _stopping;
        private System.Threading.Thread _thread;
        public string EngineName { get { return "WINDIVERT"; } }
        public bool IsRunning { get { return _thread != null && !_stopping; } }
        public event EventHandler<CapturedTcpPayloadEventArgs> PayloadReceived;
        public event EventHandler<string> StatusChanged;

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr WinDivertOpen(string filter, uint layer, short priority, ulong flags);
        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)] private static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint readLen, IntPtr address);
        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)] private static extern bool WinDivertShutdown(IntPtr handle, uint how);
        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)] private static extern bool WinDivertClose(IntPtr handle);

        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null) return;
                _handle = WinDivertOpen(Filter, LayerNetwork, 0, 0);
                if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("WinDivertOpen failed. Win32=" + error + ".");
                }
                _address = Marshal.AllocHGlobal((int)AddressBufferSize);
                _stopping = false;
                _thread = new System.Threading.Thread(ReceiveLoop) { IsBackground = true, Name = "KINOJO-WinDivert-Capture" };
                _thread.Start();
                RaiseStatus("WINDIVERT · 전체 네트워크 어댑터 감시 중");
            }
        }

        private void ReceiveLoop()
        {
            var packet = new byte[65535];
            while (!_stopping)
            {
                uint length; bool ok;
                try { ok = WinDivertRecv(_handle, packet, (uint)packet.Length, out length, _address); }
                catch { break; }
                if (!ok) { if (!_stopping) RaiseStatus("WinDivert 수신 중단"); break; }
                CapturedTcpPayloadEventArgs payload;
                if (TryReadTcpPayload(packet, (int)length, out payload)) PayloadReceived?.Invoke(this, payload);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopping = true;
                if (_handle != IntPtr.Zero) { try { WinDivertShutdown(_handle, ShutdownReceive); } catch { } }
            }
            var thread = _thread; if (thread != null && thread.IsAlive) thread.Join(1000);
            lock (_gate)
            {
                if (_handle != IntPtr.Zero) { try { WinDivertClose(_handle); } catch { } _handle = IntPtr.Zero; }
                if (_address != IntPtr.Zero) { try { Marshal.FreeHGlobal(_address); } catch { } _address = IntPtr.Zero; }
                _thread = null;
            }
        }

        private static bool TryReadTcpPayload(byte[] packet, int length, out CapturedTcpPayloadEventArgs value)
        {
            value = null;
            if (packet == null || length < 40) return false;
            var version = (packet[0] >> 4) & 0x0F;
            int ipHeader; int totalLength; int protocolOffset; int sourceOffset; int destinationOffset;
            if (version == 4)
            {
                ipHeader = (packet[0] & 0x0F) * 4;
                if (ipHeader < 20 || length < ipHeader + 20 || packet[9] != 6) return false;
                totalLength = ReadUInt16(packet, 2); if (totalLength <= 0 || totalLength > length) totalLength = length;
                protocolOffset = ipHeader; sourceOffset = 12; destinationOffset = 16;
            }
            else if (version == 6)
            {
                if (length < 60 || packet[6] != 6) return false;
                ipHeader = 40; totalLength = Math.Min(length, 40 + ReadUInt16(packet, 4)); protocolOffset = 40; sourceOffset = 8; destinationOffset = 24;
            }
            else return false;

            var srcPort = ReadUInt16(packet, protocolOffset); var dstPort = ReadUInt16(packet, protocolOffset + 2);
            var sequence = ReadUInt32(packet, protocolOffset + 4);
            var tcpHeader = ((packet[protocolOffset + 12] >> 4) & 0x0F) * 4;
            var payloadOffset = protocolOffset + tcpHeader;
            if (tcpHeader < 20 || payloadOffset >= totalLength) return false;
            var payloadLength = totalLength - payloadOffset;
            var bytes = new byte[payloadLength]; Buffer.BlockCopy(packet, payloadOffset, bytes, 0, payloadLength);
            string source; string destination;
            if (version == 4)
            {
                source = new IPAddress(new byte[] { packet[sourceOffset], packet[sourceOffset + 1], packet[sourceOffset + 2], packet[sourceOffset + 3] }).ToString();
                destination = new IPAddress(new byte[] { packet[destinationOffset], packet[destinationOffset + 1], packet[destinationOffset + 2], packet[destinationOffset + 3] }).ToString();
            }
            else
            {
                var src = new byte[16]; var dst = new byte[16]; Buffer.BlockCopy(packet, sourceOffset, src, 0, 16); Buffer.BlockCopy(packet, destinationOffset, dst, 0, 16);
                source = new IPAddress(src).ToString(); destination = new IPAddress(dst).ToString();
            }
            value = new CapturedTcpPayloadEventArgs(bytes, DateTime.UtcNow, source + ":" + srcPort, destination + ":" + dstPort, sequence);
            return true;
        }

        private static ushort ReadUInt16(byte[] value, int offset) { return (ushort)((value[offset] << 8) | value[offset + 1]); }
        private static uint ReadUInt32(byte[] value, int offset) { return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) | ((uint)value[offset + 2] << 8) | value[offset + 3]; }
        private void RaiseStatus(string text) { StatusChanged?.Invoke(this, text); }
        public void Dispose() { Stop(); }
    }

    internal sealed class CaptureFallbackService : INetworkCaptureService
    {
        private INetworkCaptureService _active;
        public string EngineName { get { return _active == null ? "NONE" : _active.EngineName; } }
        public bool IsRunning { get { return _active != null && _active.IsRunning; } }
        public event EventHandler<CapturedTcpPayloadEventArgs> PayloadReceived;
        public event EventHandler<string> StatusChanged;

        public void Start()
        {
            Stop();
            Exception npcapError = null;
            try
            {
                Activate(new NpcapCaptureService());
                _active.Start();
                RaiseStatus("NPCAP 우선 캡처 활성화");
                return;
            }
            catch (Exception ex)
            {
                npcapError = ex;
                Stop();
            }
            try
            {
                Activate(new WinDivertCaptureService());
                _active.Start();
                RaiseStatus("NPCAP 사용 불가 → WINDIVERT 대체 캡처 활성화");
            }
            catch (Exception ex)
            {
                Stop();
                throw new InvalidOperationException("Npcap과 WinDivert 캡처를 모두 시작하지 못했습니다. Npcap: " + (npcapError == null ? "알 수 없음" : npcapError.Message) + " / WinDivert: " + ex.Message, ex);
            }
        }

        private void Activate(INetworkCaptureService value)
        {
            _active = value;
            _active.PayloadReceived += ForwardPayload;
            _active.StatusChanged += ForwardStatus;
        }
        private void ForwardPayload(object sender, CapturedTcpPayloadEventArgs e) { PayloadReceived?.Invoke(this, e); }
        private void ForwardStatus(object sender, string text) { RaiseStatus(text); }
        private void RaiseStatus(string text) { StatusChanged?.Invoke(this, text); }
        public void Stop()
        {
            if (_active == null) return;
            try { _active.PayloadReceived -= ForwardPayload; _active.StatusChanged -= ForwardStatus; _active.Stop(); _active.Dispose(); } catch { }
            _active = null;
        }
        public void Dispose() { Stop(); }
    }

    internal sealed class TcpReassemblyService
    {
        private sealed class FlowState
        {
            public bool HasExpected;
            public uint Expected;
            public SortedDictionary<uint, byte[]> Pending = new SortedDictionary<uint, byte[]>();
            public DateTime LastSeenUtc;
            public string ConnectionKey;
            public string Direction;
        }
        private readonly object _gate = new object();
        private readonly Dictionary<string, FlowState> _flows = new Dictionary<string, FlowState>(StringComparer.OrdinalIgnoreCase);
        public event EventHandler<GameFrameEventArgs> StreamData;

        public void Push(CapturedTcpPayloadEventArgs segment)
        {
            if (segment == null || segment.Payload.Length == 0) return;
            lock (_gate)
            {
                FlowState state;
                if (!_flows.TryGetValue(segment.FlowKey, out state))
                {
                    state = new FlowState { ConnectionKey = segment.ConnectionKey, Direction = segment.Direction };
                    _flows[segment.FlowKey] = state;
                }
                state.LastSeenUtc = segment.TimestampUtc;
                if (!state.HasExpected)
                {
                    state.HasExpected = true;
                    state.Expected = unchecked(segment.SequenceNumber + (uint)segment.Payload.Length);
                    Emit(segment.Payload, segment.TimestampUtc, segment.FlowKey, state);
                }
                else if (segment.SequenceNumber == state.Expected)
                {
                    state.Expected = unchecked(state.Expected + (uint)segment.Payload.Length);
                    Emit(segment.Payload, segment.TimestampUtc, segment.FlowKey, state);
                    FlushPending(state, segment.TimestampUtc, segment.FlowKey);
                }
                else if (SequenceLess(segment.SequenceNumber, state.Expected))
                {
                    var overlap = unchecked((int)(state.Expected - segment.SequenceNumber));
                    if (overlap < segment.Payload.Length)
                    {
                        var tail = new byte[segment.Payload.Length - overlap]; Buffer.BlockCopy(segment.Payload, overlap, tail, 0, tail.Length);
                        state.Expected = unchecked(state.Expected + (uint)tail.Length); Emit(tail, segment.TimestampUtc, segment.FlowKey, state); FlushPending(state, segment.TimestampUtc, segment.FlowKey);
                    }
                }
                else if (!state.Pending.ContainsKey(segment.SequenceNumber))
                {
                    state.Pending[segment.SequenceNumber] = segment.Payload;
                    if (state.Pending.Count > 256) state.Pending.Remove(state.Pending.Keys.First());
                }
                Cleanup(segment.TimestampUtc);
            }
        }

        private void FlushPending(FlowState state, DateTime timestampUtc, string flowKey)
        {
            while (state.Pending.ContainsKey(state.Expected))
            {
                var payload = state.Pending[state.Expected]; state.Pending.Remove(state.Expected);
                state.Expected = unchecked(state.Expected + (uint)payload.Length); Emit(payload, timestampUtc, flowKey, state);
            }
        }
        private void Emit(byte[] bytes, DateTime timestampUtc, string flowKey, FlowState state)
        {
            StreamData?.Invoke(this, new GameFrameEventArgs(bytes, timestampUtc, flowKey, state.ConnectionKey, state.Direction));
        }
        private void Cleanup(DateTime now)
        {
            foreach (var key in _flows.Where(pair => (now - pair.Value.LastSeenUtc) > TimeSpan.FromMinutes(3)).Select(pair => pair.Key).ToList()) _flows.Remove(key);
        }
        private static bool SequenceLess(uint left, uint right) { return unchecked((int)(left - right)) < 0; }
    }

    internal interface IGameFrameDecoder
    {
        string DecoderType { get; }
        string DecoderVersion { get; }
        bool IsValidated { get; }
        bool TryDecode(GameFrameEventArgs frame, IList<CombatEvent> events);
    }

    internal sealed class AionBinaryFrameDecoder : IGameFrameDecoder
    {
        private const ushort ClientHelloOpcode = 0x3610;
        private const ushort ServerHelloOpcode = 0x3611;
        private sealed class TransportState
        {
            public readonly Dictionary<string, List<byte>> PrefixByDirection = new Dictionary<string, List<byte>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, byte[]> ParserProbeTailByDirection = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, byte[]> PartyProbeTailByDirection = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public bool ClientHelloSeen;
            public bool ServerHelloSeen;
            public bool Announced;
            public bool LateAttached;
            public bool ParserEnvelopeSeen;
            public bool PartyRosterSeen;
            public bool ParserEnvelopeCandidateAnnounced;
            public string LastPartyRosterSignature = "";
            public DateTime LastSeenUtc;
        }
        private sealed class PartyMemberProbe
        {
            public int Offset;
            public int ServerRaw;
            public string Name;
            public int ClassRaw;
            public int Level;
        }
        private readonly object _gate = new object();
        private readonly Dictionary<string, TransportState> _connections = new Dictionary<string, TransportState>(StringComparer.OrdinalIgnoreCase);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public string DecoderType { get { return "BINARY_UNVALIDATED"; } }
        public string DecoderVersion { get { return "aion2-late-attach-party-roster-probe-3"; } }
        public bool IsValidated { get { return false; } }
        public event EventHandler<string> AionConnectionIdentified;
        public event EventHandler<string> ParserEnvelopeCandidateObserved;
        public event EventHandler<string> PartyRosterCandidateObserved;
        public event EventHandler<PartyRosterDetectedEventArgs> PartyRosterDetected;

        public bool TryDecode(GameFrameEventArgs frame, IList<CombatEvent> events)
        {
            if (frame == null || frame.Frame == null || frame.Frame.Length == 0 || String.IsNullOrWhiteSpace(frame.ConnectionKey)) return false;
            string identified = null;
            string parserEnvelopeCandidate = null;
            string partyRosterCandidate = null;
            PartyRosterDetectedEventArgs partyRosterDetected = null;
            lock (_gate)
            {
                TransportState state;
                if (!_connections.TryGetValue(frame.ConnectionKey, out state))
                {
                    state = new TransportState();
                    _connections[frame.ConnectionKey] = state;
                }
                state.LastSeenUtc = frame.TimestampUtc;
                List<byte> prefix;
                if (!state.PrefixByDirection.TryGetValue(frame.Direction, out prefix))
                {
                    prefix = new List<byte>(4);
                    state.PrefixByDirection[frame.Direction] = prefix;
                }
                for (var index = 0; index < frame.Frame.Length && prefix.Count < 4; index++) prefix.Add(frame.Frame[index]);
                if (prefix.Count == 4)
                {
                    var declaredLength = (ushort)(prefix[0] | (prefix[1] << 8));
                    var opcode = (ushort)(prefix[2] | (prefix[3] << 8));
                    if (declaredLength >= 4 && declaredLength <= 4096)
                    {
                        if (opcode == ClientHelloOpcode) state.ClientHelloSeen = true;
                        if (opcode == ServerHelloOpcode) state.ServerHelloSeen = true;
                    }
                }
                int candidateLength;
                if (TryObserveParserEnvelopeCandidate(state, frame.Direction, frame.Frame, out candidateLength))
                {
                    state.ParserEnvelopeSeen = true;
                    if (!state.ParserEnvelopeCandidateAnnounced)
                    {
                        state.ParserEnvelopeCandidateAnnounced = true;
                        parserEnvelopeCandidate = frame.ConnectionKey + "|" + frame.Direction + "|" + candidateLength.ToString(CultureInfo.InvariantCulture);
                    }
                }
                string rosterSignature;
                string rosterDetail;
                List<DetectedPartyMember> rosterMembers;
                if (TryObservePartyRosterCandidate(state, frame.Direction, frame.Frame, out rosterSignature, out rosterDetail, out rosterMembers) &&
                    !String.Equals(state.LastPartyRosterSignature, rosterSignature, StringComparison.Ordinal))
                {
                    state.PartyRosterSeen = true;
                    state.LastPartyRosterSignature = rosterSignature;
                    partyRosterCandidate = frame.ConnectionKey + "|" + frame.Direction + "|" + rosterDetail;
                    partyRosterDetected = new PartyRosterDetectedEventArgs
                    {
                        ConnectionKey = frame.ConnectionKey,
                        Direction = frame.Direction,
                        Members = rosterMembers
                    };
                }
                if (!state.Announced && state.ClientHelloSeen && state.ServerHelloSeen)
                {
                    state.Announced = true;
                    identified = frame.ConnectionKey;
                }
                else if (!state.Announced && state.ParserEnvelopeSeen && state.PartyRosterSeen)
                {
                    state.Announced = true;
                    state.LateAttached = true;
                    identified = "LATE_ATTACH|" + frame.ConnectionKey;
                }
                if (partyRosterDetected != null) partyRosterDetected.LateAttached = state.LateAttached;
                foreach (var key in _connections.Where(pair => (frame.TimestampUtc - pair.Value.LastSeenUtc) > TimeSpan.FromMinutes(3)).Select(pair => pair.Key).ToList())
                    _connections.Remove(key);
            }
            if (identified != null) AionConnectionIdentified?.Invoke(this, identified);
            if (parserEnvelopeCandidate != null) ParserEnvelopeCandidateObserved?.Invoke(this, parserEnvelopeCandidate);
            if (partyRosterCandidate != null) PartyRosterCandidateObserved?.Invoke(this, partyRosterCandidate);
            if (partyRosterDetected != null) PartyRosterDetected?.Invoke(this, partyRosterDetected);

            // 운영 안전 규칙: 0x3610/0x3611 초기 교환으로 AION2 흐름을 확인하더라도,
            // 후속 변환 계층과 전투 필드가 검증되기 전에는 CombatEvent를 만들지 않는다.
            return false;
        }

        private static bool TryObservePartyRosterCandidate(
            TransportState state,
            string direction,
            byte[] bytes,
            out string signature,
            out string detail,
            out List<DetectedPartyMember> detectedMembers)
        {
            signature = "";
            detail = "";
            detectedMembers = null;
            byte[] tail;
            if (!state.PartyProbeTailByDirection.TryGetValue(direction ?? "", out tail)) tail = new byte[0];
            var combined = new byte[tail.Length + bytes.Length];
            if (tail.Length > 0) Buffer.BlockCopy(tail, 0, combined, 0, tail.Length);
            Buffer.BlockCopy(bytes, 0, combined, tail.Length, bytes.Length);

            var records = new List<PartyMemberProbe>();
            for (var offset = 0; offset < combined.Length; offset++)
            {
                PartyMemberProbe record;
                if (TryReadPartyMemberProbe(combined, offset, out record)) records.Add(record);
            }

            List<PartyMemberProbe> best = null;
            var bestLevelIsCurrentCap = false;
            var bestSpan = Int32.MaxValue;
            for (var startIndex = 0; startIndex < records.Count; startIndex++)
            {
                var first = records[startIndex];
                var unique = new Dictionary<string, PartyMemberProbe>(StringComparer.Ordinal);
                var previousOffset = first.Offset;
                for (var currentIndex = startIndex; currentIndex < records.Count; currentIndex++)
                {
                    var current = records[currentIndex];
                    if (current.Offset - first.Offset > 320) break;
                    if (current.Offset - previousOffset > 96) break;
                    previousOffset = current.Offset;
                    if (!unique.ContainsKey(current.Name)) unique[current.Name] = current;
                    if (unique.Count >= 6) break;
                }
                if (unique.Count < 4) continue;
                var members = unique.Values.OrderBy(member => member.Offset).ToList();
                var span = members[members.Count - 1].Offset - members[0].Offset;
                var levelIsCurrentCap = members.Any(member => member.Level == 50);
                if (best == null ||
                    members.Count > best.Count ||
                    (members.Count == best.Count && levelIsCurrentCap && !bestLevelIsCurrentCap) ||
                    (members.Count == best.Count && levelIsCurrentCap == bestLevelIsCurrentCap && span < bestSpan))
                {
                    best = members;
                    bestLevelIsCurrentCap = levelIsCurrentCap;
                    bestSpan = span;
                }
            }

            SavePartyProbeTail(state, direction, combined);
            if (best == null) return false;

            var signatureBuilder = new StringBuilder();
            var detailBuilder = new StringBuilder();
            detailBuilder.Append("members=").Append(best.Count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < best.Count; index++)
            {
                var member = best[index];
                if (index > 0) signatureBuilder.Append("|");
                signatureBuilder
                    .Append(member.ServerRaw.ToString(CultureInfo.InvariantCulture)).Append(":")
                    .Append(member.Name).Append(":")
                    .Append(member.ClassRaw.ToString(CultureInfo.InvariantCulture)).Append(":")
                    .Append(member.Level.ToString(CultureInfo.InvariantCulture));
                detailBuilder
                    .Append(";slot=").Append((index + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(",name=").Append(member.Name)
                    .Append(",server_raw=").Append(member.ServerRaw.ToString(CultureInfo.InvariantCulture))
                    .Append(",class_raw=").Append(member.ClassRaw.ToString(CultureInfo.InvariantCulture))
                    .Append(",level=").Append(member.Level.ToString(CultureInfo.InvariantCulture));
            }
            signature = signatureBuilder.ToString();
            detail = detailBuilder.ToString();
            detectedMembers = best.Select((member, index) => new DetectedPartyMember
            {
                Slot = index + 1,
                ServerRaw = member.ServerRaw,
                CharacterName = member.Name,
                ClassRaw = member.ClassRaw,
                Level = member.Level
            }).ToList();
            return true;
        }

        private static bool TryReadPartyMemberProbe(byte[] data, int offset, out PartyMemberProbe record)
        {
            record = null;
            if (data == null || offset < 0 || offset >= data.Length) return false;
            if (offset > 0 && data[offset - 1] >= 0x80) return false;

            int serverRaw;
            int serverBytes;
            if (!TryRead7BitValue(data, offset, out serverRaw, out serverBytes) || serverRaw < 128 || serverRaw > 4095) return false;
            var nameLengthOffset = offset + serverBytes;
            if (nameLengthOffset >= data.Length) return false;
            var nameLength = data[nameLengthOffset];
            if (nameLength < 3 || nameLength > 36) return false;

            var nameOffset = nameLengthOffset + 1;
            var fieldsOffset = nameOffset + nameLength;
            if (fieldsOffset + 12 > data.Length) return false;
            string name;
            try { name = StrictUtf8.GetString(data, nameOffset, nameLength); }
            catch { return false; }
            if (name.Length < 1 || name.Length > 12) return false;
            for (var index = 0; index < name.Length; index++)
                if (!Char.IsLetterOrDigit(name[index])) return false;

            var classRaw = ReadUInt32LittleEndian(data, fieldsOffset);
            var level = ReadUInt32LittleEndian(data, fieldsOffset + 4);
            if (classRaw < 1 || classRaw > 64 || level < 1 || level > 100) return false;
            record = new PartyMemberProbe
            {
                Offset = offset,
                ServerRaw = serverRaw,
                Name = name,
                ClassRaw = (int)classRaw,
                Level = (int)level
            };
            return true;
        }

        private static bool TryRead7BitValue(byte[] data, int offset, out int value, out int bytesRead)
        {
            value = 0;
            bytesRead = 0;
            var shift = 0;
            for (var count = 0; count < 5; count++)
            {
                var position = offset + count;
                if (position >= data.Length) return false;
                var current = data[position];
                value |= (current & 0x7F) << shift;
                bytesRead++;
                if ((current & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private static uint ReadUInt32LittleEndian(byte[] data, int offset)
        {
            return (uint)data[offset] |
                ((uint)data[offset + 1] << 8) |
                ((uint)data[offset + 2] << 16) |
                ((uint)data[offset + 3] << 24);
        }

        private static void SavePartyProbeTail(TransportState state, string direction, byte[] combined)
        {
            const int maximumTail = 4096;
            var length = Math.Min(maximumTail, combined.Length);
            var tail = new byte[length];
            if (length > 0) Buffer.BlockCopy(combined, combined.Length - length, tail, 0, length);
            state.PartyProbeTailByDirection[direction ?? ""] = tail;
        }

        private static bool TryObserveParserEnvelopeCandidate(TransportState state, string direction, byte[] bytes, out int declaredLength)
        {
            declaredLength = 0;
            byte[] tail;
            if (!state.ParserProbeTailByDirection.TryGetValue(direction ?? "", out tail)) tail = new byte[0];
            var combined = new byte[tail.Length + bytes.Length];
            if (tail.Length > 0) Buffer.BlockCopy(tail, 0, combined, 0, tail.Length);
            Buffer.BlockCopy(bytes, 0, combined, tail.Length, bytes.Length);

            for (var index = 0; index + 2 < combined.Length; index++)
            {
                // INGMeter Parser가 내부 메시지 후보를 찾을 때 사용하는 0x3641 표식.
                // 표식 뒤 값은 7-bit varint이며, 의미가 검증되기 전에는 전투 이벤트로 변환하지 않는다.
                if (combined[index] != 0x41 || combined[index + 1] != 0x36) continue;
                var value = 0;
                var shift = 0;
                for (var count = 0; count < 5 && index + 2 + count < combined.Length; count++)
                {
                    var current = combined[index + 2 + count];
                    value |= (current & 0x7F) << shift;
                    if ((current & 0x80) == 0)
                    {
                        if (value >= 1 && value <= 99999)
                        {
                            declaredLength = value;
                            SaveParserProbeTail(state, direction, combined);
                            return true;
                        }
                        break;
                    }
                    shift += 7;
                }
            }
            SaveParserProbeTail(state, direction, combined);
            return false;
        }

        private static void SaveParserProbeTail(TransportState state, string direction, byte[] combined)
        {
            const int maximumTail = 6;
            var length = Math.Min(maximumTail, combined.Length);
            var tail = new byte[length];
            if (length > 0) Buffer.BlockCopy(combined, combined.Length - length, tail, 0, length);
            state.ParserProbeTailByDirection[direction ?? ""] = tail;
        }
    }

    internal sealed class JsonCombatFrameDecoder : IGameFrameDecoder
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public string DecoderType { get { return "JSON_PREVIEW"; } }
        public string DecoderVersion { get { return "preview-only-1"; } }
        public bool IsValidated { get { return false; } }
        public bool TryDecode(GameFrameEventArgs frame, IList<CombatEvent> events)
        {
            if (frame == null || frame.Frame.Length == 0 || events == null || (frame.Frame[0] != (byte)'{' && frame.Frame[0] != (byte)'[')) return false;
            try
            {
                var row = _json.DeserializeObject(Encoding.UTF8.GetString(frame.Frame)) as Dictionary<string, object>;
                if (row == null) return false;
                CombatEventKind kind; if (!Enum.TryParse(Text(row, "kind"), true, out kind)) return false;
                events.Add(new CombatEvent { Kind = kind, TimestampUtc = frame.TimestampUtc, ActorId = Text(row, "actorId"), PlatformCharacterId = Text(row, "platformCharacterId"), ActorName = Text(row, "actorName"), ActorServerId = Text(row, "actorServerId"), ActorServer = Text(row, "actorServer"), ActorClassKey = Text(row, "actorClassKey"), ActorClass = Text(row, "actorClass"), TargetId = Text(row, "targetId"), TargetName = Text(row, "targetName"), Damage = Number(row, "damage"), CurrentHp = Number(row, "currentHp"), MaxHp = Number(row, "maxHp"), IsBoss = Bool(row, "isBoss"), PartyNumber = (int)Number(row, "partyNumber"), PartySlot = (int)Number(row, "partySlot"), CombatPower = Number(row, "combatPower"), DungeonName = Text(row, "dungeonName"), DifficultyName = Text(row, "difficultyName"), ContentName = Text(row, "contentName") });
                return true;
            }
            catch { return false; }
        }
        private static string Text(Dictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
        private static long Number(Dictionary<string, object> row, string key) { long parsed; return Int64.TryParse(Text(row, key), out parsed) ? parsed : 0; }
        private static bool Bool(Dictionary<string, object> row, string key) { bool parsed; return Boolean.TryParse(Text(row, key), out parsed) && parsed; }
    }

    internal sealed class CombatCaptureCoordinator : IDisposable
    {
        private const long MaximumPrebufferBytes = 8L * 1024L * 1024L;
        private static readonly TimeSpan MaximumPrebufferAge = TimeSpan.FromMinutes(2);
        private readonly CaptureFallbackService _capture = new CaptureFallbackService();
        private readonly TcpReassemblyService _reassembly = new TcpReassemblyService();
        private readonly AionBinaryFrameDecoder _decoder = new AionBinaryFrameDecoder();
        private readonly DiagnosticFrameCollector _fixtureCollector = new DiagnosticFrameCollector();
        private readonly object _gate = new object();
        private readonly Queue<CapturedTcpPayloadEventArgs> _prebuffer = new Queue<CapturedTcpPayloadEventArgs>();
        private long _prebufferBytes;
        private System.Threading.Timer _retryTimer;
        private bool _desiredRunning;
        private bool _starting;
        private bool _disposed;
        private bool _trafficConfirmed;
        private int _retryAttempt;
        private int _streamChunkCount;
        private string _lastFlowKey = "";

        public event EventHandler<CombatEvent> CombatEventReceived;
        public event EventHandler<PartyRosterDetectedEventArgs> PartyRosterDetected;
        public event EventHandler<string> StatusChanged;
        public event EventHandler<string> DiagnosticStatusChanged;
        public CaptureRuntimeInfo RuntimeInfo { get; private set; }

        public CombatCaptureCoordinator()
        {
            RuntimeInfo = new CaptureRuntimeInfo
            {
                CaptureEngine = "NONE",
                CaptureMode = "ACTUAL",
                DecoderType = _decoder.DecoderType,
                DecoderVersion = _decoder.DecoderVersion,
                DecoderValidated = _decoder.IsValidated,
                UploadEligible = false
            };
            _capture.PayloadReceived += delegate(object sender, CapturedTcpPayloadEventArgs e)
            {
                lock (_gate)
                {
                    BufferRecentPayload(e);
                    _fixtureCollector.Append(e);
                }
                _reassembly.Push(e);
            };
            _capture.StatusChanged += delegate(object sender, string text) { RaiseDiagnostic(text); };
            _reassembly.StreamData += OnStreamData;
            _decoder.AionConnectionIdentified += delegate(object sender, string connectionKey)
            {
                var lateAttach = connectionKey != null && connectionKey.StartsWith("LATE_ATTACH|", StringComparison.Ordinal);
                var actualKey = lateAttach ? connectionKey.Substring("LATE_ATTACH|".Length) : connectionKey;
                RaiseStatus(lateAttach ? "AION2 실행 중 연결 합류 · 정보 확인 중" : "AION2 연결 확인 · Decoder 분석 중");
                RaiseDiagnostic(lateAttach
                    ? "AION2 transport identified by late attach evidence (0x3641 envelope + party roster). Connection=" + actualKey + "."
                    : "AION2 transport identified by bidirectional 0x3610/0x3611 handshake. Connection=" + actualKey + ".");
            };
            _decoder.ParserEnvelopeCandidateObserved += delegate(object sender, string detail)
            {
                RaiseDiagnostic("AION2 parser envelope candidate observed (0x3641 + varint). Detail=" + detail + ". Decoder remains unvalidated.");
            };
            _decoder.PartyRosterCandidateObserved += delegate(object sender, string detail)
            {
                RaiseDiagnostic("AION2 party roster candidate observed. Detail=" + detail + ". Raw server/class ids remain unvalidated.");
            };
            _decoder.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                PartyRosterDetected?.Invoke(this, value);
            };
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _desiredRunning = true;
            }
            TryStart();
        }

        public void Restart()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _desiredRunning = true;
                _retryAttempt = 0;
                _trafficConfirmed = false;
                _streamChunkCount = 0;
                _lastFlowKey = "";
                CancelRetryLocked();
            }
            try { _capture.Stop(); }
            catch { }
            RuntimeInfo.CaptureEngine = "NONE";
            RaiseStatus("게임 연결 준비 중");
            RaiseDiagnostic("Capture restart requested by administrator.");
            TryStart();
        }

        public bool IsFixtureCaptureActive { get { return _fixtureCollector.IsActive; } }

        public string ToggleFixtureCapture()
        {
            if (_fixtureCollector.IsActive)
            {
                var stoppedDirectory = _fixtureCollector.Stop();
                RaiseDiagnostic("Diagnostic packet fixture stopped. Directory=" + stoppedDirectory + ".");
                return stoppedDirectory;
            }
            string startedDirectory;
            CapturedTcpPayloadEventArgs[] buffered;
            lock (_gate)
            {
                buffered = _prebuffer.ToArray();
                startedDirectory = _fixtureCollector.Start();
                foreach (var segment in buffered) _fixtureCollector.Append(segment);
                _fixtureCollector.AddMarker("CAPTURE_STARTED", "PREBUFFER_CHUNKS=" + buffered.Length);
            }
            RaiseDiagnostic("Diagnostic packet fixture started. Maximum=20 minutes/64 MiB. Prebuffer=" + buffered.Length + " chunks. Directory=" + startedDirectory + ".");
            return startedDirectory;
        }

        public bool AddFixtureMarker(string marker)
        {
            var added = _fixtureCollector.AddMarker(marker, "");
            if (added) RaiseDiagnostic("Diagnostic marker added: " + marker + ".");
            return added;
        }

        private void BufferRecentPayload(CapturedTcpPayloadEventArgs segment)
        {
            if (segment == null || segment.Payload == null || segment.Payload.Length == 0) return;
            lock (_gate)
            {
                _prebuffer.Enqueue(segment);
                _prebufferBytes += segment.Payload.Length;
                var cutoff = segment.TimestampUtc - MaximumPrebufferAge;
                while (_prebuffer.Count > 0 &&
                    (_prebufferBytes > MaximumPrebufferBytes || _prebuffer.Peek().TimestampUtc < cutoff))
                {
                    _prebufferBytes -= _prebuffer.Dequeue().Payload.Length;
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _desiredRunning = false;
                CancelRetryLocked();
            }
            try { _capture.Stop(); }
            catch { }
            RuntimeInfo.CaptureEngine = "NONE";
        }

        private void TryStart()
        {
            lock (_gate)
            {
                if (_disposed || !_desiredRunning || _starting || _capture.IsRunning) return;
                _starting = true;
                CancelRetryLocked();
            }

            try
            {
                _capture.Start();
                RuntimeInfo.CaptureEngine = _capture.EngineName;
                lock (_gate) _retryAttempt = 0;
                RaiseStatus("게임 연결 대기 중");
                RaiseDiagnostic(RuntimeInfo.CaptureEngine + " capture started. Decoder=" + RuntimeInfo.DecoderType + "/" + RuntimeInfo.DecoderVersion + ".");
            }
            catch (Exception ex)
            {
                RuntimeInfo.CaptureEngine = "NONE";
                RaiseStatus("게임 연결 준비 중");
                RaiseDiagnostic("Capture start failed: " + ex.Message);
                ScheduleRetry();
            }
            finally
            {
                lock (_gate) _starting = false;
            }
        }

        private void ScheduleRetry()
        {
            int delaySeconds;
            lock (_gate)
            {
                if (_disposed || !_desiredRunning) return;
                var delays = new[] { 5, 15, 30, 60 };
                delaySeconds = delays[Math.Min(_retryAttempt, delays.Length - 1)];
                _retryAttempt++;
                CancelRetryLocked();
                _retryTimer = new System.Threading.Timer(delegate { TryStart(); }, null, TimeSpan.FromSeconds(delaySeconds), System.Threading.Timeout.InfiniteTimeSpan);
            }
            RaiseDiagnostic("Next capture retry in " + delaySeconds + " seconds.");
        }

        private void CancelRetryLocked()
        {
            if (_retryTimer == null) return;
            try { _retryTimer.Dispose(); }
            catch { }
            _retryTimer = null;
        }

        private void OnStreamData(object sender, GameFrameEventArgs frame)
        {
            _streamChunkCount++;
            _lastFlowKey = frame.FlowKey;
            RuntimeInfo.FlowKey = frame.FlowKey;
            RuntimeInfo.CaptureEngine = _capture.EngineName;
            if (!_trafficConfirmed)
            {
                _trafficConfirmed = true;
                RaiseStatus("게임 연결됨 · 전투 대기");
                RaiseDiagnostic(RuntimeInfo.CaptureEngine + " TCP traffic confirmed. Flow=" + frame.FlowKey + ".");
            }

            var events = new List<CombatEvent>();
            if (!_decoder.TryDecode(frame, events))
            {
                if (_streamChunkCount == 1 || _streamChunkCount % 100 == 0)
                    RaiseDiagnostic(RuntimeInfo.CaptureEngine + " TCP reassembly active. Decoder fixture pending. chunk=" + _streamChunkCount + ", flow=" + _lastFlowKey + ".");
                return;
            }
            RuntimeInfo.UploadEligible = RuntimeInfo.CaptureMode == "ACTUAL" && _decoder.IsValidated && String.Equals(_decoder.DecoderType, "BINARY_VALIDATED", StringComparison.OrdinalIgnoreCase);
            foreach (var value in events) CombatEventReceived?.Invoke(this, value);
        }

        private void RaiseStatus(string text)
        {
            StatusChanged?.Invoke(this, text);
        }

        private void RaiseDiagnostic(string text)
        {
            DiagnosticStatusChanged?.Invoke(this, text);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _desiredRunning = false;
                CancelRetryLocked();
            }
            try { _capture.Stop(); }
            catch { }
            _capture.Dispose();
            _fixtureCollector.Dispose();
        }
    }
}
