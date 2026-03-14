using System.Text;
using OpenMeasure.Bus;

namespace OpenMeasure.Format;

/// <summary>
/// Binary serialization of BusChannelDefinition for storage in group properties.
/// </summary>
internal static class BusMetadataEncoder
{
    private const byte FormatVersion = 1;

    public static byte[] Encode(BusChannelDefinition def)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(FormatVersion);
        WriteBusConfig(bw, def.BusConfig);
        WriteString(bw, def.RawFrameChannelName);
        WriteString(bw, def.TimestampChannelName);

        // Frames
        bw.Write(def.Frames.Count);
        foreach (var frame in def.Frames)
            WriteFrame(bw, frame, def.BusConfig.BusType);

        // Value tables
        bw.Write(def.ValueTables.Count);
        foreach (var vt in def.ValueTables)
        {
            WriteString(bw, vt.Name);
            bw.Write(vt.Entries.Count);
            foreach (var (val, desc) in vt.Entries)
            {
                bw.Write(val);
                WriteString(bw, desc);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    public static BusChannelDefinition Decode(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        byte version = data[offset++];
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported bus metadata version: {version}");

        var busConfig = ReadBusConfig(data, ref offset);
        string rawChannelName = ReadString(data, ref offset);
        string tsChannelName = ReadString(data, ref offset);

        var def = new BusChannelDefinition
        {
            BusConfig = busConfig,
            RawFrameChannelName = rawChannelName,
            TimestampChannelName = tsChannelName,
        };

        int frameCount = ReadInt32(data, ref offset);
        for (int i = 0; i < frameCount; i++)
            def.Frames.Add(ReadFrame(data, ref offset, busConfig.BusType));

        int vtCount = ReadInt32(data, ref offset);
        for (int i = 0; i < vtCount; i++)
        {
            string vtName = ReadString(data, ref offset);
            int entryCount = ReadInt32(data, ref offset);
            var entries = new Dictionary<long, string>(entryCount);
            for (int j = 0; j < entryCount; j++)
            {
                long val = ReadInt64(data, ref offset);
                string desc = ReadString(data, ref offset);
                entries[val] = desc;
            }
            def.ValueTables.Add(new ValueTable { Name = vtName, Entries = entries });
        }

        return def;
    }

    // --- BusConfig ---

    private static void WriteBusConfig(BinaryWriter bw, BusConfig config)
    {
        bw.Write((byte)config.BusType);
        switch (config)
        {
            case CanBusConfig can:
                bw.Write(can.IsExtendedId);
                bw.Write(can.BaudRate);
                break;
            case CanFdBusConfig canFd:
                bw.Write(canFd.IsExtendedId);
                bw.Write(canFd.ArbitrationBaudRate);
                bw.Write(canFd.DataBaudRate);
                break;
            case LinBusConfig lin:
                bw.Write(lin.BaudRate);
                bw.Write(lin.LinVersion);
                break;
            case FlexRayBusConfig fr:
                bw.Write(fr.CycleTimeUs);
                bw.Write(fr.MacroticksPerCycle);
                break;
            case EthernetBusConfig:
            case MostBusConfig:
                // no additional fields
                break;
        }
    }

    private static BusConfig ReadBusConfig(ReadOnlySpan<byte> data, ref int offset)
    {
        var busType = (BusType)data[offset++];
        return busType switch
        {
            BusType.Can => new CanBusConfig
            {
                IsExtendedId = ReadBool(data, ref offset),
                BaudRate = ReadInt32(data, ref offset),
            },
            BusType.CanFd => new CanFdBusConfig
            {
                IsExtendedId = ReadBool(data, ref offset),
                ArbitrationBaudRate = ReadInt32(data, ref offset),
                DataBaudRate = ReadInt32(data, ref offset),
            },
            BusType.Lin => new LinBusConfig
            {
                BaudRate = ReadInt32(data, ref offset),
                LinVersion = data[offset++],
            },
            BusType.FlexRay => new FlexRayBusConfig
            {
                CycleTimeUs = ReadInt32(data, ref offset),
                MacroticksPerCycle = ReadInt32(data, ref offset),
            },
            BusType.Ethernet => new EthernetBusConfig(),
            BusType.Most => new MostBusConfig(),
            _ => throw new InvalidDataException($"Unknown bus type: {busType}"),
        };
    }

    // --- Frame ---

    private static void WriteFrame(BinaryWriter bw, FrameDefinition frame, BusType busType)
    {
        WriteString(bw, frame.Name);
        bw.Write(frame.FrameId);
        bw.Write(frame.PayloadLength);
        bw.Write((byte)frame.Direction);
        bw.Write((ushort)frame.Flags);

        // Bus-specific frame fields via polymorphism
        switch (frame)
        {
            case CanFrameDefinition can:
                bw.Write(can.IsExtendedId);
                break;
            case CanFdFrameDefinition canFd:
                bw.Write(canFd.IsExtendedId);
                bw.Write(canFd.BitRateSwitch);
                bw.Write(canFd.ErrorStateIndicator);
                break;
            case LinFrameDefinition lin:
                bw.Write(lin.Nad);
                bw.Write((byte)lin.ChecksumType);
                break;
            case FlexRayFrameDefinition fr:
                bw.Write(fr.CycleCount);
                bw.Write((byte)fr.Channel);
                break;
            case EthernetFrameDefinition eth:
                WriteBytes(bw, eth.MacSource, 6);
                WriteBytes(bw, eth.MacDestination, 6);
                bw.Write(eth.VlanId);
                bw.Write(eth.EtherType);
                break;
            case MostFrameDefinition most:
                bw.Write(most.FunctionBlock);
                bw.Write(most.InstanceId);
                bw.Write(most.FunctionId);
                break;
        }

        // Direct signals
        bw.Write(frame.Signals.Count);
        foreach (var sig in frame.Signals)
            WriteSignal(bw, sig);

        // PDUs
        bw.Write(frame.Pdus.Count);
        foreach (var pdu in frame.Pdus)
            WritePdu(bw, pdu);
    }

    private static FrameDefinition ReadFrame(ReadOnlySpan<byte> data, ref int offset, BusType busType)
    {
        string name = ReadString(data, ref offset);
        uint frameId = ReadUInt32(data, ref offset);
        int payloadLen = ReadInt32(data, ref offset);
        var direction = (FrameDirection)data[offset++];
        var flags = (FrameFlags)ReadUInt16(data, ref offset);

        FrameDefinition frame = busType switch
        {
            BusType.Can => new CanFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
                IsExtendedId = ReadBool(data, ref offset),
            },
            BusType.CanFd => new CanFdFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
                IsExtendedId = ReadBool(data, ref offset),
                BitRateSwitch = ReadBool(data, ref offset),
                ErrorStateIndicator = ReadBool(data, ref offset),
            },
            BusType.Lin => new LinFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
                Nad = data[offset++],
                ChecksumType = (LinChecksumType)data[offset++],
            },
            BusType.FlexRay => new FlexRayFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
                CycleCount = data[offset++],
                Channel = (FlexRayChannel)data[offset++],
            },
            BusType.Ethernet => new EthernetFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
                MacSource = ReadBytes(data, ref offset, 6),
                MacDestination = ReadBytes(data, ref offset, 6),
                VlanId = ReadUInt16(data, ref offset),
                EtherType = ReadUInt16(data, ref offset),
            },
            BusType.Most => new MostFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
                FunctionBlock = ReadUInt16(data, ref offset),
                InstanceId = data[offset++],
                FunctionId = ReadUInt16(data, ref offset),
            },
            _ => new CanFrameDefinition
            {
                Name = name, FrameId = frameId, PayloadLength = payloadLen,
                Direction = direction, Flags = flags,
            },
        };

        int sigCount = ReadInt32(data, ref offset);
        for (int i = 0; i < sigCount; i++)
            frame.Signals.Add(ReadSignal(data, ref offset));

        int pduCount = ReadInt32(data, ref offset);
        for (int i = 0; i < pduCount; i++)
            frame.Pdus.Add(ReadPdu(data, ref offset));

        return frame;
    }

    // --- PDU ---

    private static void WritePdu(BinaryWriter bw, PduDefinition pdu)
    {
        WriteString(bw, pdu.Name);
        bw.Write(pdu.PduId);
        bw.Write(pdu.ByteOffset);
        bw.Write(pdu.Length);
        bw.Write(pdu.IsContainerPdu);

        // E2E
        bw.Write(pdu.E2EProtection != null);
        if (pdu.E2EProtection is { } e2e)
        {
            bw.Write((byte)e2e.Profile);
            bw.Write(e2e.CrcStartBit);
            bw.Write(e2e.CrcBitLength);
            bw.Write(e2e.CounterStartBit);
            bw.Write(e2e.CounterBitLength);
            bw.Write(e2e.DataId);
            bw.Write(e2e.CrcPolynomial);
        }

        // SecOC
        bw.Write(pdu.SecOc != null);
        if (pdu.SecOc is { } secOc)
        {
            bw.Write((byte)secOc.Algorithm);
            bw.Write(secOc.FreshnessValueStartBit);
            bw.Write(secOc.FreshnessValueTruncatedLength);
            bw.Write(secOc.FreshnessValueFullLength);
            bw.Write((byte)secOc.FreshnessType);
            bw.Write(secOc.MacStartBit);
            bw.Write(secOc.MacTruncatedLength);
            bw.Write(secOc.MacFullLength);
            bw.Write(secOc.AuthenticPayloadLength);
            bw.Write(secOc.DataId);
            bw.Write(secOc.AuthenticationBuildAttempts);
            bw.Write(secOc.UseFreshnessValueManager);
            bw.Write(secOc.KeyId);
        }

        // Multiplexing
        bw.Write(pdu.Multiplexing != null);
        if (pdu.Multiplexing is { } mux)
        {
            WriteString(bw, mux.MultiplexerSignalName);
            bw.Write(mux.MuxGroups.Count);
            foreach (var (val, names) in mux.MuxGroups)
            {
                bw.Write(val);
                bw.Write(names.Count);
                foreach (var n in names)
                    WriteString(bw, n);
            }
        }

        // Signals
        bw.Write(pdu.Signals.Count);
        foreach (var sig in pdu.Signals)
            WriteSignal(bw, sig);

        // Contained PDUs
        bw.Write(pdu.ContainedPdus.Count);
        foreach (var cpdu in pdu.ContainedPdus)
        {
            WriteString(bw, cpdu.Name);
            bw.Write(cpdu.HeaderId);
            bw.Write(cpdu.Length);
            bw.Write(cpdu.Signals.Count);
            foreach (var sig in cpdu.Signals)
                WriteSignal(bw, sig);
        }
    }

    private static PduDefinition ReadPdu(ReadOnlySpan<byte> data, ref int offset)
    {
        string name = ReadString(data, ref offset);
        uint pduId = ReadUInt32(data, ref offset);
        int byteOff = ReadInt32(data, ref offset);
        int length = ReadInt32(data, ref offset);
        bool isContainer = ReadBool(data, ref offset);

        // E2E
        E2EProtection? e2e = null;
        if (ReadBool(data, ref offset))
        {
            e2e = new E2EProtection
            {
                Profile = (E2EProfile)data[offset++],
                CrcStartBit = ReadInt32(data, ref offset),
                CrcBitLength = ReadInt32(data, ref offset),
                CounterStartBit = ReadInt32(data, ref offset),
                CounterBitLength = ReadInt32(data, ref offset),
                DataId = ReadUInt32(data, ref offset),
                CrcPolynomial = ReadUInt32(data, ref offset),
            };
        }

        // SecOC
        SecOcConfig? secOc = null;
        if (ReadBool(data, ref offset))
        {
            secOc = new SecOcConfig
            {
                Algorithm = (SecOcAlgorithm)data[offset++],
                FreshnessValueStartBit = ReadInt32(data, ref offset),
                FreshnessValueTruncatedLength = ReadInt32(data, ref offset),
                FreshnessValueFullLength = ReadInt32(data, ref offset),
                FreshnessType = (FreshnessValueType)data[offset++],
                MacStartBit = ReadInt32(data, ref offset),
                MacTruncatedLength = ReadInt32(data, ref offset),
                MacFullLength = ReadInt32(data, ref offset),
                AuthenticPayloadLength = ReadInt32(data, ref offset),
                DataId = ReadUInt32(data, ref offset),
                AuthenticationBuildAttempts = ReadInt32(data, ref offset),
                UseFreshnessValueManager = ReadBool(data, ref offset),
                KeyId = ReadUInt32(data, ref offset),
            };
        }

        // Multiplexing
        MultiplexConfig? mux = null;
        if (ReadBool(data, ref offset))
        {
            string muxSignalName = ReadString(data, ref offset);
            int groupCount = ReadInt32(data, ref offset);
            var muxGroups = new Dictionary<long, List<string>>(groupCount);
            for (int i = 0; i < groupCount; i++)
            {
                long val = ReadInt64(data, ref offset);
                int nameCount = ReadInt32(data, ref offset);
                var names = new List<string>(nameCount);
                for (int j = 0; j < nameCount; j++)
                    names.Add(ReadString(data, ref offset));
                muxGroups[val] = names;
            }
            mux = new MultiplexConfig { MultiplexerSignalName = muxSignalName, MuxGroups = muxGroups };
        }

        var pdu = new PduDefinition
        {
            Name = name,
            PduId = pduId,
            ByteOffset = byteOff,
            Length = length,
            IsContainerPdu = isContainer,
            E2EProtection = e2e,
            SecOc = secOc,
            Multiplexing = mux,
        };

        int sigCount = ReadInt32(data, ref offset);
        for (int i = 0; i < sigCount; i++)
            pdu.Signals.Add(ReadSignal(data, ref offset));

        int cpduCount = ReadInt32(data, ref offset);
        for (int i = 0; i < cpduCount; i++)
        {
            string cpduName = ReadString(data, ref offset);
            uint headerId = ReadUInt32(data, ref offset);
            int cpduLen = ReadInt32(data, ref offset);
            var cpdu = new ContainedPduDefinition { Name = cpduName, HeaderId = headerId, Length = cpduLen };
            int csigCount = ReadInt32(data, ref offset);
            for (int j = 0; j < csigCount; j++)
                cpdu.Signals.Add(ReadSignal(data, ref offset));
            pdu.ContainedPdus.Add(cpdu);
        }

        return pdu;
    }

    // --- Signal ---

    private static void WriteSignal(BinaryWriter bw, SignalDefinition sig)
    {
        WriteString(bw, sig.Name);
        bw.Write(sig.StartBit);
        bw.Write(sig.BitLength);
        bw.Write((byte)sig.ByteOrder);
        bw.Write((byte)sig.DataType);
        bw.Write(sig.Factor);
        bw.Write(sig.Offset);

        // MinMax
        byte minMaxFlags = 0;
        if (sig.MinValue.HasValue) minMaxFlags |= 1;
        if (sig.MaxValue.HasValue) minMaxFlags |= 2;
        bw.Write(minMaxFlags);
        if (sig.MinValue.HasValue) bw.Write(sig.MinValue.Value);
        if (sig.MaxValue.HasValue) bw.Write(sig.MaxValue.Value);

        // Unit
        bw.Write(sig.Unit != null);
        if (sig.Unit != null) WriteString(bw, sig.Unit);

        // MUX
        bw.Write(sig.IsMultiplexer);
        bw.Write(sig.MultiplexCondition != null);
        if (sig.MultiplexCondition is { } mc)
            WriteMuxCondition(bw, mc);

        // Value descriptions
        bw.Write(sig.ValueDescriptions?.Count ?? 0);
        if (sig.ValueDescriptions != null)
        {
            foreach (var (val, desc) in sig.ValueDescriptions)
            {
                bw.Write(val);
                WriteString(bw, desc);
            }
        }
    }

    private static SignalDefinition ReadSignal(ReadOnlySpan<byte> data, ref int offset)
    {
        string name = ReadString(data, ref offset);
        int startBit = ReadInt32(data, ref offset);
        int bitLength = ReadInt32(data, ref offset);
        var byteOrder = (ByteOrder)data[offset++];
        var dataType = (SignalDataType)data[offset++];
        double factor = ReadFloat64(data, ref offset);
        double sigOffset = ReadFloat64(data, ref offset);

        byte minMaxFlags = data[offset++];
        double? min = (minMaxFlags & 1) != 0 ? ReadFloat64(data, ref offset) : null;
        double? max = (minMaxFlags & 2) != 0 ? ReadFloat64(data, ref offset) : null;

        bool hasUnit = ReadBool(data, ref offset);
        string? unit = hasUnit ? ReadString(data, ref offset) : null;

        bool isMultiplexer = ReadBool(data, ref offset);
        MultiplexCondition? muxCond = null;
        if (ReadBool(data, ref offset))
            muxCond = ReadMuxCondition(data, ref offset);

        int vdCount = ReadInt32(data, ref offset);
        Dictionary<long, string>? valueDescs = vdCount > 0 ? new(vdCount) : null;
        for (int i = 0; i < vdCount; i++)
        {
            long val = ReadInt64(data, ref offset);
            string desc = ReadString(data, ref offset);
            valueDescs![val] = desc;
        }

        return new SignalDefinition
        {
            Name = name,
            StartBit = startBit,
            BitLength = bitLength,
            ByteOrder = byteOrder,
            DataType = dataType,
            Factor = factor,
            Offset = sigOffset,
            MinValue = min,
            MaxValue = max,
            Unit = unit,
            IsMultiplexer = isMultiplexer,
            MultiplexCondition = muxCond,
            ValueDescriptions = valueDescs,
        };
    }

    // --- MuxCondition (recursive) ---

    private static void WriteMuxCondition(BinaryWriter bw, MultiplexCondition mc)
    {
        WriteString(bw, mc.MultiplexerSignalName);
        bw.Write(mc.LowValue);
        bw.Write(mc.HighValue);
        bw.Write(mc.ParentCondition != null);
        if (mc.ParentCondition != null)
            WriteMuxCondition(bw, mc.ParentCondition);
    }

    private static MultiplexCondition ReadMuxCondition(ReadOnlySpan<byte> data, ref int offset)
    {
        string muxName = ReadString(data, ref offset);
        long low = ReadInt64(data, ref offset);
        long high = ReadInt64(data, ref offset);
        MultiplexCondition? parent = null;
        if (ReadBool(data, ref offset))
            parent = ReadMuxCondition(data, ref offset);
        return new MultiplexCondition
        {
            MultiplexerSignalName = muxName,
            LowValue = low,
            HighValue = high,
            ParentCondition = parent,
        };
    }

    // --- Primitives ---

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteBytes(BinaryWriter bw, byte[]? data, int fixedLen)
    {
        if (data != null && data.Length == fixedLen)
            bw.Write(data);
        else
            bw.Write(new byte[fixedLen]);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        int len = ReadInt32(data, ref offset);
        var str = Encoding.UTF8.GetString(data.Slice(offset, len));
        offset += len;
        return str;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        int val = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        return val;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        uint val = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        return val;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        long val = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        return val;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset)
    {
        ushort val = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        return val;
    }

    private static double ReadFloat64(ReadOnlySpan<byte> data, ref int offset)
    {
        double val = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]);
        offset += 8;
        return val;
    }

    private static bool ReadBool(ReadOnlySpan<byte> data, ref int offset)
    {
        return data[offset++] != 0;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var bytes = data.Slice(offset, count).ToArray();
        offset += count;
        return bytes;
    }
}
