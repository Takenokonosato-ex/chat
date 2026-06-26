using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace WifiDirectTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Wi-Fi Direct Standalone Test ===");
            Console.WriteLine("1: ホストモード (待受)");
            Console.WriteLine("2: クライアントモード (接続)");
            Console.Write("モードを選択してください (1 or 2): ");
            var mode = Console.ReadLine();

            if (mode == "1")
            {
                await RunHostAsync();
            }
            else if (mode == "2")
            {
                await RunClientAsync();
            }
            else
            {
                Console.WriteLine("無効な入力です。終了します。");
            }
        }

        static async Task RunHostAsync()
        {
            Console.WriteLine($"\n[ホストモード] このPCの名前は '{Environment.MachineName}' です。");
            Console.WriteLine("Wi-Fi Directのパブリッシュを開始します...");

            var publisher = new WiFiDirectAdvertisementPublisher();
            publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Intensive;
            
            var listener = new WiFiDirectConnectionListener();
            listener.ConnectionRequested += async (s, e) =>
            {
                var request = e.GetConnectionRequest();
                Console.WriteLine($"\n[ホスト] 接続要求を受信: {request.DeviceInformation.Name}");
                
                try
                {
                    await EnsurePairedAsync(request.DeviceInformation);
                    var device = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
                    if (device == null)
                    {
                        Console.WriteLine("[ホスト] デバイスの取得に失敗しました。");
                        return;
                    }

                    var endpoints = device.GetConnectionEndpointPairs();
                    if (endpoints.Count > 0)
                    {
                        var endpoint = endpoints[0];
                        Console.WriteLine($"[ホスト] エンドポイント取得: {endpoint.LocalHostName.DisplayName} : 50001");
                        
                        var socketListener = new StreamSocketListener();
                        socketListener.ConnectionReceived += async (sender, args) =>
                        {
                            Console.WriteLine("[ホスト] ソケット接続を受信しました！");
                            var reader = new DataReader(args.Socket.InputStream);
                            reader.InputStreamOptions = InputStreamOptions.Partial;
                            await reader.LoadAsync(1024);
                            var text = reader.ReadString(reader.UnconsumedBufferLength);
                            Console.WriteLine($"[ホスト] メッセージを受信: {text}");
                            Console.WriteLine(">>> 接続テスト成功！ <<<");
                        };
                        await socketListener.BindEndpointAsync(endpoint.LocalHostName, "50001");
                        Console.WriteLine("[ホスト] 接続待機中...");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ホスト] エラー発生: {ex.Message}");
                }
            };

            publisher.Start();
            Console.WriteLine("[ホスト] 待受を開始しました。クライアントから接続してください。(Enterで終了)");
            Console.ReadLine();
            
            publisher.Stop();
        }

        static async Task RunClientAsync()
        {
            Console.Write("\n接続先PCの名前を入力してください: ");
            var targetName = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(targetName))
            {
                Console.WriteLine("PC名が空です。終了します。");
                return;
            }

            Console.WriteLine($"[クライアント] '{targetName}' を探索します...");

            var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
            var watcher = DeviceInformation.CreateWatcher(selector, Array.Empty<string>());
            
            var tcs = new TaskCompletionSource<DeviceInformation>();

            watcher.Added += (s, e) =>
            {
                Console.WriteLine($"[探索] 発見: {e.Name} ({e.Id})");
                if (e.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[探索] ターゲットPCを発見しました！");
                    tcs.TrySetResult(e);
                }
            };

            watcher.Start();

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            watcher.Stop();

            if (completedTask == timeoutTask)
            {
                Console.WriteLine("[クライアント] タイムアウト: 指定されたPCが見つかりませんでした。");
                return;
            }

            var targetDevice = await tcs.Task;
            Console.WriteLine($"[クライアント] {targetDevice.Name} に接続を試みます...");

            try
            {
                await EnsurePairedAsync(targetDevice);
                var device = await WiFiDirectDevice.FromIdAsync(targetDevice.Id);
                if (device == null)
                {
                    Console.WriteLine("[クライアント] デバイスの取得に失敗しました。");
                    return;
                }

                var endpoints = device.GetConnectionEndpointPairs();
                if (endpoints.Count > 0)
                {
                    var endpoint = endpoints[0];
                    Console.WriteLine($"[クライアント] エンドポイント取得: {endpoint.RemoteHostName.DisplayName}");
                    
                    // 少し待機してから接続
                    await Task.Delay(2000);
                    
                    var socket = new StreamSocket();
                    await socket.ConnectAsync(endpoint.RemoteHostName, "50001");
                    Console.WriteLine("[クライアント] ソケット接続成功！テストメッセージを送信します...");
                    
                    var writer = new DataWriter(socket.OutputStream);
                    writer.WriteString($"Hello from {Environment.MachineName}");
                    await writer.StoreAsync();
                    
                    Console.WriteLine(">>> 送信完了。接続テスト成功！ <<<");
                }
                else
                {
                    Console.WriteLine("[クライアント] エンドポイントの取得に失敗しました。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[クライアント] エラー発生: {ex.Message}");
            }

            Console.WriteLine("終了するにはEnterを押してください...");
            Console.ReadLine();
        }

        static async Task EnsurePairedAsync(DeviceInformation deviceInformation)
        {
            if (deviceInformation.Pairing.IsPaired)
            {
                Console.WriteLine("[ペアリング] 既にペアリング済みです。");
                return;
            }

            Console.WriteLine("[ペアリング] カスタムペアリングを開始します...");
            var customPairing = deviceInformation.Pairing.Custom;
            
            void CustomPairing_PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                Console.WriteLine($"[ペアリング] リクエスト受信: {args.PairingKind}");
                if (args.PairingKind == DevicePairingKinds.ProvidePin)
                {
                    args.Accept("0000");
                }
                else
                {
                    args.Accept();
                }
            }

            customPairing.PairingRequested += CustomPairing_PairingRequested;

            var result = await customPairing.PairAsync(
                DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ProvidePin | DevicePairingKinds.DisplayPin);

            customPairing.PairingRequested -= CustomPairing_PairingRequested;

            Console.WriteLine($"[ペアリング] 結果: {result.Status}");
            
            if (result.Status != DevicePairingResultStatus.Paired && result.Status != DevicePairingResultStatus.AlreadyPaired)
            {
                throw new InvalidOperationException($"Pairing failed: {result.Status}");
            }
        }
    }
}
