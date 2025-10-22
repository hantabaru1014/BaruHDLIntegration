# BaruHDLIntegration

In-Game Integration for https://github.com/hantabaru1014/baru-reso-headless-controller

## インストール
Releasesにあるzipの中身をそのままResoniteのディレクトリにコピーしてください

### Google.Protobuf.dll について
Resoniteに同梱されている Google.Protobuf.dll は古いバージョンでこのModにどうしても必要な型を含んでいないので上書きしています。
Google.Protobuf.dll は OmniceptTrackingDriver(HP Reverb G2 Omnicept Editionのアイトラ？) が間接的に依存しているだけなので、上書きしてもほとんど問題ないはずという考えです。問題があったら報告お願いします。
The Splittening以前はrml_libsに入れるだけで良かったんですが、.NET9になってからライブラリの依存解決が厳格になった？影響か上書き不可避です。
