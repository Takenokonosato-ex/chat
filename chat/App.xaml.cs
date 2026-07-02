using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// WinUI とプロジェクト構成については、
// http://aka.ms/winui-project-info を参照してください。

namespace chat
{
    /// <summary>
    /// 既定の Application クラスに、このアプリ固有の動作を追加します。
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// アプリケーションのシングルトン オブジェクトを初期化します。
        /// ここが main() または WinMain() に相当する最初の実行箇所です。
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// アプリケーション起動時に呼び出されます。
        /// </summary>
        /// <param name="args">起動要求とプロセスの詳細です。</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
