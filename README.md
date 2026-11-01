# Gocgun.Cache

> **Beta.** This package is in beta: the public API may change between releases.

Byte cache for a single process plus Redis.

`GetAsync`, `SetAsync`, and `RemoveAsync` store a `byte[]`. The caller serializes protobuf (or anything else) before `SetAsync`. This package does not parse those bytes.

L1 is `IMemoryCache`. L2 is `StackExchange.Redis` 2.x. `RemoveAsync` deletes both layers and publishes a pub/sub notice so other processes drop their L1 entry. `RemoveByPrefixAsync` deletes matching L2 keys with `SCAN` (`IServer.KeysAsync`). It does not send `KEYS`.

There are no cache tags, no stampede lock, and no tenant prefix. A `KeyPrefix` option is an application prefix only.

License: AGPL-3.0-only. See `LICENSE`. Copyright holder exception: `LICENSE-EXCEPTION`.
