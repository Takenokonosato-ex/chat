# P2P Chat Application

## 概要
Wi-Fi Direct と Bluetooth Low Energy (BLE) を活用した、サーバー不要のP2P（ピアツーピア）チャットアプリケーションです。Windows (WinUI 3) と Android (Jetpack Compose) のデバイス間で、ローカルネットワークを通じた直接通信を実現します。

## プロジェクト構成
本リポジトリは主に以下の3つのコンポーネントから構成されています。

- **`chat/`**
  Windows向けのデスクトップクライアントです。モダンなUIフレームワークである **WinUI 3 (Windows App SDK)** と **C# (.NET 8.0)** を使用して開発されています。Micaエフェクトなどを取り入れた美しいチャットUIを備えています。
  
- **`chat_android/`**
  Android向けのクライアントアプリです。**Kotlin** と **Jetpack Compose** を使用して開発されており、最新のAndroid UI開発パラダイムを採用しています。（minSdk 35 / API Level 35以降）

- **`WifiDirectTest/`**
  Wi-Fi DirectのAPIや接続処理を検証・テストするための C# プロジェクトです。

## 主な機能
- **完全なサーバーレス通信**: インターネット上のサーバーを介さず、デバイス同士が直接接続してチャットを行います。オフライン環境でも通信可能です。
- **BLEによるデバイス発見**: 近くにあるデバイス（ピア）の探索とアドバタイズには Bluetooth Low Energy (BLE) を使用し、省電力かつ迅速な発見を実現します。
- **Wi-Fi Directによる高速通信**: メッセージやデータの送受信にはWi-Fi Directを利用したSocket通信を行い、安定した高速なデータのやり取りを行います。
- **クロスプラットフォーム対応**: Windows PCとAndroidスマートフォンの間でシームレスな接続とチャットをサポートします。

## 開発環境・必須要件

### Windows版 (`chat/`)
- OS: Windows 10 (Build 17763以降) または Windows 11
- 開発ツール: Visual Studio 2022 (UWP / Windows App SDK 開発ワークロード)
- フレームワーク: .NET 8.0
- ハードウェア要件: Wi-Fi および Bluetooth(BLE) 対応モジュール

### Android版 (`chat_android/`)
- OS要件: Android 15 (API Level 35) 以降を推奨
- 開発ツール: Android Studio (最新版)
- ハードウェア要件: Wi-Fi Direct および Bluetooth 対応デバイス
- 必要な権限: Bluetooth (SCAN, ADVERTISE, CONNECT), Wi-Fi (ACCESS, CHANGE), NEARBY_WIFI_DEVICES, 位置情報 など

## 使い方
1. **Windows**: Visual Studioで `chat.slnx` または `chat/chat.csproj` を開き、ビルド・実行します。
2. **Android**: Android Studioで `chat_android/` フォルダをプロジェクトとして開き、実機にインストールします。
3. 双方のアプリを起動し、必要な権限を許可した後、BLEによる探索を開始してデバイスを検出し、Wi-Fi Direct接続を確立してチャットを開始します。

※通信を行うには、双方のデバイスでWi-FiとBluetoothが有効になっている必要があります。

