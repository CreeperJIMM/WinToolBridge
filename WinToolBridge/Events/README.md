# Event subscriptions

`Events` sits alongside `Commands`. `Handlers/` contains event-specific handlers; the generic subscription infrastructure stays in the root of `Events`.

Commands use the existing DAT request/ACK/result flow:

- `EVENT:ON`, params `{ "event": "NAME" }`: returns `{ "event": "NAME", "subscriptionId": "...", "enabled": true }`. Repeated ON returns the same subscription.
- `EVENT:OFF`, params `{ "event": "NAME" }`: returns `{ "event": "NAME", "enabled": false }`. Repeated OFF succeeds.
- `EVENT:RESET`, params omitted, null or `{}`: returns `{ "cleared": 0 }` with the number removed. Only affects the originating connection.

Register backend-defined names with `server.Events.Register("NAME")` before allowing subscriptions. Unknown names and invalid parameters return the existing correlated ERROR result.

The tray application registers `SYS:INFO_CHANGED` through `SystemInfoChangedHandler`. A hidden top-level window listens for battery percentage, AC/DC power changes, basic power status, resume and volume arrival/removal notifications. Each trigger refreshes `memory`, `battery`, `storage`, `uptimeSeconds` and `timestamp` together, preserving the existing system-info field formats and units. No periodic queries run. A 300 ms notification window coalesces bursts; changes received during a query schedule another snapshot. Queries are skipped when nobody subscribes. The application unregisters native notifications and cancels the worker on exit.

Storage is the existing ready local drive/volume list, not a physical device count. Memory usage and disk free space do not refresh merely because their values changed; they refresh on a supported notification or a manual query. Initial data comes from `SYS:GET_DETAILS` after subscribing. The frontend extrapolates uptime locally and recalibrates on snapshots.

`NET:INFO_CHANGED` publishes `{ network, wifi, timestamp }`, reusing `NetworkInfoProvider` and the existing GET_DETAILS/GET_WIFI_DETAILS contracts. Sources are IPv4/IPv6 interface, unicast address and route notifications, WinRT network status, and WLAN ACM/MSM notifications. MSM (signal/roaming) falls back to ACM if access is denied; failures are logged without changing permissions. Sources only enqueue notifications, with the same 300 ms burst coalescing and no periodic queries. Notifications are unregistered and the worker canceled on exit.

The network page subscribes before initial reads, re-subscribes on a fresh connection, and protects snapshots from older requests and out-of-order notifications. Listening ports are queried only on initial/manual refresh; DNS benchmarks remain user-triggered. DNS server configuration and negotiated speed are included in each network snapshot, but coverage of isolated changes depends on Windows notifications. Wi-Fi RF details retain the existing scan-cache and permission limitations; this event does not start Wi-Fi scans or guarantee continuous RSSI updates.

Publish using `await server.Events.PublishAsync("NAME", data, cancellationToken)` and handle failures through the publisher's logging flow. This awaits initial send attempts, not client ACKs. Notifications use:

```json
{
  "type": "EVT",
  "msgId": "b_dat_...",
  "payload": {
    "event": "NAME",
    "subscriptionId": "...",
    "sequence": 1,
    "timestamp": 1791500000000,
    "data": {}
  }
}
```

The client ACKs msgId using the existing ACK format. EVT uses the same bounded retry policy as DAT. Deduplicate retries by msgId (or subscriptionId plus sequence). Sequence starts at 1 for each new subscription. It is a notification counter, not a source revision.

OFF, RESET, disconnect and shutdown cancel subscription delivery and retries. Already transmitted frames cannot be recalled: clients must ignore revoked subscription IDs. Notifications may arrive before the ON result, so clients should buffer them until the subscription ID is known. New connections have no subscriptions; subscribing again is an explicit client decision.

`USB:TREE_CHANGED` is an invalidation event: it carries only `{ revision, timestamp }`. The client then calls `USB:GET_TREE` or `USB:GET_NODE_DETAILS`. See [USB protocol](../Services/Usb/README.md) for node formats, version rules and native enumeration limitations.

The frontend handles EVT through `src/lib/wintool/events` (`bridgeEvents.listen/on/off/reset`). No HTTP endpoint is included. USB frontend integration is not part of this backend implementation.

Validation: build the solution, then verify event subscription, cancellation and reconnection through the frontend. After reconnecting, confirm that subscriptions from the previous connection are no longer active.
