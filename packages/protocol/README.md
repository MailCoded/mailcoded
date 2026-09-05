# @mailcoded/protocol

TypeScript types for the mailcoded JSON-RPC 2.0 surface: every DTO, the method and notification
names, the string vocabularies, the error codes, and a typed `RpcMethodMap` pairing each method with
its params and result. Types only — no runtime code, no dependencies.

`src/generated.ts` is **generated** from the C# records in `src/Mailcoded.Protocol` by
`tools/Mailcoded.Protocol.TypeScript` and checked in. It is regenerated with

    npm run gen:types          # at the repository root, or in this directory

and a test in `tests/Mailcoded.Core.Tests` fails whenever the checked-in file no longer matches the
assembly, so a DTO change cannot land without its TypeScript. Do not edit the file by hand.

Compatibility is governed by `PROTOCOL_VERSION`, which a client compares against the
`protocolVersion` a daemon answers on `initialize` — not by this package's npm version. The human
reference for the wire is [docs/rpc.md](../../docs/rpc.md).

Publishing to npm is a human act and is not automated anywhere in this repository.
