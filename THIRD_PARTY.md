# Protocol references and acknowledgements

The C# integration is an independent implementation of BLE wire formats documented in the following public projects. No BikeControl application code or private dependencies are included.

- [FreeWheel Shift](https://github.com/pgmcgee/freewheel-shift): trainer FC82 service, RideOn handshake, protobuf field structure and 24 gear ratios. Reference files: `mobile/lib/vshift_protocol.dart`, `components/freewheel/proto/vshift.proto`, `components/freewheel/src/vshift.c`. The project reports testing on KICKR CORE 2; that does not certify this app.
- [Zwiftboard](https://github.com/thomaschampagne/zwiftboard): Click 2 service UUIDs, manufacturer device identifiers, activation sequence, active-low button bitmap, keepalive and authorization caveats. Reference: `internal/zwift/protocol.go`.
- [OpenBikeControl / BikeControl](https://github.com/OpenBikeControl/bikecontrol): cross-check of Click 2 manufacturer identifiers and the authorization requirement. References: `lib/bluetooth/devices/zwift/constants.dart` and `zwift_clickv2.dart`.

Acknowledgements and MIT notices for the protocol references:

## FreeWheel Shift

Copyright 2026 Parker McGee

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Zwiftboard

MIT License

Copyright (c) 2026 Thomas Champagne

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
