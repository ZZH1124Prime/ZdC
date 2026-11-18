# TrafikRuler source provenance

Upstream: https://github.com/li4inka/trafik-ruler
Commit: 9868c18b3426f8d5c2d34c0b651eaf0956bed7bd
Copyright: 2026 kulich Software
License: GPL-3.0-or-later (upstream source metadata); full GPL v3 text accompanies this file.

Selected Core and Native sources are preserved under src/ZdC.Core/Upstream, with their original namespace. The UI, configuration store, automatic connection resets and upstream RuleEngine are excluded. ZdC supplies a small diagnostic localization adapter. ZdC modifications include complete process tuples, PID creation identity checks, reserved-port NAT with bounded endpoint leases and endpoint-independent UDP mapping, and PacketRouter error cleanup. Newly added NAT allocator is invoked only during explicit capture sessions; offline checks inject an in-memory allocator. No WinDivert binary or driver is included or installed.

Redistribution of the resulting linked program must meet GPL requirements, including corresponding source and license notices. WinDivert, if later distributed, has its own LGPL-3.0 terms; see THIRD_PARTY_NOTICES.md.
