# Complete Specification

The complete binary format specification is maintained in the repository.

[View SPECIFICATION.md on GitHub →](https://github.com/vreitenbach/MeasFlow/blob/main/SPECIFICATION.md){ .md-button .md-button--primary }

## What's Included

The specification document provides:

- **Complete binary layout** with byte offsets and field descriptions
- **Data type encoding** for all supported types
- **Segment structure** for metadata and data segments
- **Bus metadata format** for CAN, LIN, FlexRay, Ethernet
- **Compression** format and algorithms
- **Conformance requirements** and validation rules
- **Versioning** strategy for backward compatibility
- **Extension points** for future additions

## Quick Reference

For a high-level overview of the file format, see the [File Format Guide](guide/file-format.md).

## Implementation Notes

The specification is designed to be:

- **Language-agnostic**: Implementable in any programming language
- **Self-contained**: No external dependencies required
- **Testable**: Clear validation rules for conformance testing
- **Extensible**: Reserved fields for backward-compatible additions

## Contributing

Found an issue or ambiguity in the specification?

[Open an issue on GitHub →](https://github.com/vreitenbach/MeasFlow/issues){ .md-button }
