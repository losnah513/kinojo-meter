#!/usr/bin/env python3
"""KINOJO Meter fixture에서 AION2 전송·Parser·파티 후보를 재현 검증한다."""

import argparse
import csv
from collections import defaultdict
from pathlib import Path


def read_rows(path):
    with path.open("r", encoding="utf-8") as source:
        return [
            row
            for row in csv.DictReader(
                (line for line in source if not line.startswith("#")), delimiter="\t"
            )
            if row.get("sequence")
        ]


def rebuild_streams(fixture):
    binary = (fixture / "frames.bin").read_bytes()
    grouped = defaultdict(list)
    for row in read_rows(fixture / "frames.tsv"):
        grouped[(row["connection_id"], row["direction"])].append(
            (
                int(row["sequence"]),
                int(row["offset"]),
                int(row["length"]),
            )
        )

    streams = {}
    for key, segments in grouped.items():
        seen_sequences = set()
        rebuilt = bytearray()
        for sequence, offset, length in sorted(segments):
            if sequence in seen_sequences:
                continue
            seen_sequences.add(sequence)
            rebuilt.extend(binary[offset : offset + length])
        streams[key] = bytes(rebuilt)
    return streams


def read_varint(data, offset):
    value = 0
    shift = 0
    for count in range(5):
        position = offset + count
        if position >= len(data):
            return None
        current = data[position]
        value |= (current & 0x7F) << shift
        if current < 0x80:
            return value, count + 1
        shift += 7
    return None


def candidates(data):
    position = 0
    while True:
        position = data.find(b"\x41\x36", position)
        if position < 0:
            return
        decoded = read_varint(data, position + 2)
        if decoded and 1 <= decoded[0] <= 99999:
            yield position, decoded[0], decoded[1]
        position += 1


def read_player_record(data, offset):
    if offset > 0 and data[offset - 1] >= 0x80:
        return None
    decoded_server = read_varint(data, offset)
    if not decoded_server:
        return None
    server_id, server_bytes = decoded_server
    if not 128 <= server_id <= 4095:
        return None

    name_length_offset = offset + server_bytes
    if name_length_offset >= len(data):
        return None
    name_length = data[name_length_offset]
    if not 3 <= name_length <= 36:
        return None

    name_offset = name_length_offset + 1
    fields_offset = name_offset + name_length
    if fields_offset + 12 > len(data):
        return None
    try:
        name = data[name_offset:fields_offset].decode("utf-8")
    except UnicodeDecodeError:
        return None
    if not 1 <= len(name) <= 12 or not all(character.isalnum() for character in name):
        return None

    class_id = int.from_bytes(data[fields_offset : fields_offset + 4], "little")
    level = int.from_bytes(data[fields_offset + 4 : fields_offset + 8], "little")
    if not 1 <= class_id <= 64 or not 1 <= level <= 100:
        return None
    return {
        "offset": offset,
        "server_id": server_id,
        "name": name,
        "class_id": class_id,
        "level": level,
    }


def player_records(data):
    return [
        record
        for offset in range(len(data))
        if (record := read_player_record(data, offset)) is not None
    ]


def best_roster_candidate(data):
    records = player_records(data)
    best = None
    for start_index, first in enumerate(records):
        unique = {}
        previous_offset = first["offset"]
        for current in records[start_index:]:
            if current["offset"] - first["offset"] > 320:
                break
            if current["level"] != first["level"]:
                continue
            if current["offset"] - previous_offset > 96:
                break
            previous_offset = current["offset"]
            if current["name"] not in unique:
                unique[current["name"]] = current
            if len(unique) >= 6:
                break
        members = sorted(unique.values(), key=lambda record: record["offset"])
        if not 4 <= len(members) <= 6:
            continue
        span = members[-1]["offset"] - members[0]["offset"]
        score = (len(members), first["level"] == 50, -span)
        if best is None or score > best[0]:
            best = (score, members)
    return [] if best is None else best[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("fixture", type=Path)
    arguments = parser.parse_args()
    fixture = arguments.fixture.resolve()
    streams = rebuild_streams(fixture)

    print(
        "connection_id\tdirection\tbytes\thello_3610\thello_3611\t"
        "parser_3641_candidates\tparty_members"
    )
    for (connection_id, direction), data in sorted(streams.items()):
        hello_3610 = data.count(b"\x10\x36")
        hello_3611 = data.count(b"\x11\x36")
        found = list(candidates(data))
        roster = best_roster_candidate(data)
        if hello_3610 or hello_3611 or found or roster:
            print(
                f"{connection_id}\t{direction}\t{len(data)}\t"
                f"{hello_3610}\t{hello_3611}\t{len(found)}\t{len(roster)}"
            )
            for slot, member in enumerate(roster, 1):
                print(
                    "PARTY_CANDIDATE\t"
                    f"slot={slot}\tname={member['name']}\t"
                    f"server_raw={member['server_id']}\t"
                    f"class_raw={member['class_id']}\tlevel={member['level']}\t"
                    f"offset={member['offset']}"
                )


if __name__ == "__main__":
    main()
