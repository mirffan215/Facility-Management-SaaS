# ADR-0027: Energy and utility readings (Phase 9b)
Status: Proposed. Builds on [ADR-0018](0018-vendor-portal-and-guest-identity.md) (a secret address instead of an account) and [ADR-0021](0021-reporting-and-bi-feed.md).

## Decisions
- **Meters are separate from asset meters.** An energy meter is a utility supply with a kind (electricity, water, gas, cooling, heat), a unit and a place. It may be a *cumulative register* (the reading is the total so far, with an optional roll-over value) or an *interval* meter (each reading is the use in its period). Asset meters for maintenance triggers remain in Facilities.
- **Readings are append-only.** A wrong reading is *voided* with a reason (itself append-only) and the right one added; nothing is edited or deleted. Readings from three sources are told apart: typed in, from a device, from a file.
- **Consumption** is the difference between successive valid readings (handling roll-over and ignoring voided ones), attributed to the day the interval ends in. The closing reading just after the period is looked up so the last day is counted. A gap is shown as a gap; it is not interpolated. Cost is the use times the tariff that applied on the day.
- **Devices post with their own secret.** A device is created by a manager; its secret (`fmse_…`, 256 random bits) is **shown once** and only its SHA-256 is kept. A device is tied to specific meters and cannot post for any other, nor for another organisation. A wrong, revoked or expired secret gets the same 401. The route is anonymous in the framework's sense, authenticated by that secret, is in the locked list of anonymous routes and shares the per-address budget of the other secret routes (ADR-0024). A batch is at most 500 readings; readings older than 730 days or in the future are refused; a repeated reading (same meter, time and value) is counted as a duplicate and stored once, so a device can safely resend; the same time with a different value, or a cumulative reading that goes down without a roll-over, is rejected with the reason.
- **CSV import** (up to 2 MB and 5000 rows) is a two-step: check first (nothing saved, each bad row explained), then import. Rows for unknown or archived meters are rejected, not guessed.
- **Tariffs** are per kind, with a start date, a price per unit and a currency; the price in force on a day applies to that day's use.
- **Alarms.** Two rule kinds: high use in a day and no reading for some hours. An alarm *raises* once and *clears* when the condition ends, each an append-only event, so a long excursion is one alarm, not hundreds of messages. An hourly worker evaluates the rules. Managers are told.
- **No protocols.** BACnet, Modbus and MQTT are not spoken here. A gateway on site that can make an HTTPS request can use the readings API.

## Consequences
- Alarms notify only; they do not open work orders.
- Consumption by day uses the server's UTC calendar.
- A device with a wrong clock can post readings that look old or odd; they are accepted if inside the window and can be voided.
