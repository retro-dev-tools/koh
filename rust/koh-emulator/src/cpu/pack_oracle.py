"""Pack independently captured C# CPU JSONL traces; no CPU execution."""

import json
import struct
import sys
from pathlib import Path


def pack(source, target):
    records = [json.loads(line) for line in Path(source).read_text().splitlines()]
    output = bytearray(struct.pack("<I", len(records)))
    for record in records:
        output += bytes([record["prefixed"], record["opcode"], record["variant"]])
        state = bytes.fromhex(record["state"])
        assert len(state) == 26
        output += state
        output += bytes([len(record["trace"])])
        for event in record["trace"]:
            output += struct.pack(
                "<BHBQ",
                event["kind"] == "write",
                event["address"],
                event["value"],
                event["ticks"],
            )
    Path(target).write_bytes(output)
    print(f"Packed {len(records)} C# CPU traces ({len(output)} bytes)")


if __name__ == "__main__":
    pack(*sys.argv[1:])
