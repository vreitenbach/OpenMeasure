"""MeasFlow — Python reader/writer for the .meas binary measurement format."""

from measflow.types import MeasDataType, MeasTimestamp, MeasValue
from measflow.reader import MeasReader, MeasGroup, MeasChannel, ChannelStatistics
from measflow.writer import MeasWriter, GroupWriter, ChannelWriter
from measflow.bus import (
    BusChannelDefinition, BusConfig, BusType,
    CanBusConfig, CanFdBusConfig, LinBusConfig, FlexRayBusConfig,
    EthernetBusConfig, MostBusConfig,
    FrameDefinition, CanFrameDefinition, CanFdFrameDefinition,
    LinFrameDefinition, FlexRayFrameDefinition, EthernetFrameDefinition, MostFrameDefinition,
    SignalDefinition, PduDefinition, ContainedPduDefinition,
    E2EProtection, SecOcConfig, MultiplexConfig, MultiplexCondition,
    ValueTable, FrameDirection, ByteOrder, SignalDataType,
    encode_bus_def, decode_bus_def,
)
from measflow.frames import CanFrame, LinFrame, FlexRayFrame, EthernetFrame

__all__ = [
    # Core types
    "MeasDataType", "MeasTimestamp", "MeasValue",
    # Reader
    "MeasReader", "MeasGroup", "MeasChannel", "ChannelStatistics",
    # Writer (pass compression="lz4" or compression="zstd" to MeasWriter)
    "MeasWriter", "GroupWriter", "ChannelWriter",
    # Bus metadata (§10)
    "BusChannelDefinition", "BusConfig", "BusType",
    "CanBusConfig", "CanFdBusConfig", "LinBusConfig", "FlexRayBusConfig",
    "EthernetBusConfig", "MostBusConfig",
    "FrameDefinition", "CanFrameDefinition", "CanFdFrameDefinition",
    "LinFrameDefinition", "FlexRayFrameDefinition", "EthernetFrameDefinition", "MostFrameDefinition",
    "SignalDefinition", "PduDefinition", "ContainedPduDefinition",
    "E2EProtection", "SecOcConfig", "MultiplexConfig", "MultiplexCondition",
    "ValueTable", "FrameDirection", "ByteOrder", "SignalDataType",
    "encode_bus_def", "decode_bus_def",
    # Wire frames (§11)
    "CanFrame", "LinFrame", "FlexRayFrame", "EthernetFrame",
]
