using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ScreenCastClient
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        Stream rawStream;//FFmpegからデコードデータを受け取るストリーム
        int imageWidth, imageHeight, bytePerframe;//画像を生成するのに必要なパラメータ
        int displayWidth, displayHeight;//タッチパネル座標の最大値
        NetworkStream streamToInputHost;//InputHostへのストリーム

        bool mouseDown;
        bool running;
        WriteableBitmap writeableBitmap;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartFFmpeg();
            StartInputHost();
        }

        private void StartFFmpeg()
        {
            //ポートの設定
            Exec("adb forward tcp:8080 tcp:8080");

            var inputArgs = "-framerate 60  -analyzeduration 100 -i tcp://127.0.0.1:8080";
            var outputArgs = "-f rawvideo -pix_fmt bgr24  -r 60 -flags +global_header - ";
            Process process = new Process
            {
                StartInfo =
                 {
                    FileName = "ffmpeg.exe",
                    Arguments = $"{inputArgs} {outputArgs}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,//stderrを読めるようにする
                    RedirectStandardOutput=true//stdoutを読めるようにする
                 },
                EnableRaisingEvents = true
            };
            process.ErrorDataReceived += Process_ErrorDataReceived;//stderrからはログが流れてくるので別途処理する
            process.Start();
            rawStream = process.StandardOutput.BaseStream;//stdoutからはデータが流れてくるのでストリームを取得しておく
            process.BeginErrorReadLine();
            running = true;
            Task.Run(() =>
            {
                //別スレッドで読み取り開始
                ReadRawData();
            });
        }

        //FFmpegからの標準エラー出力を読む
        private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;

            Console.WriteLine(e.Data);

            if (imageWidth == 0 && imageHeight == 0)//送られてくるサイズがまだ確定していないとき
            {
                //FFmpegの出力からサイズを抜き取る荒業
                string[] res = GetRegexResult(e.Data, @"([0-9]*?)x([0-9]*?), [0-9]*? fps");
                if (res.Length == 2)
                {
                    imageWidth = int.Parse(res[0]);
                    imageHeight = int.Parse(res[1]);
                    bytePerframe = imageWidth * imageHeight * 3;

                    if (imageWidth > imageHeight)//横向き画面の場合
                    {
                        //タッチ座標の最大値と最小値を入れ替える
                        int tmp = displayWidth;
                        displayWidth = displayHeight;
                        displayHeight = tmp;
                    }

                    Dispatcher.Invoke(() =>
                    {//UIスレッドでBitmapを作成しないと、UIに反映できない
                        writeableBitmap = new WriteableBitmap(imageWidth, imageHeight, 96, 96, PixelFormats.Bgr24, null);
                        image.Source = writeableBitmap;
                    });
                }
            }

        }

        //FFmpegからのrawStreamを読んでBitmapに書き込む
        private void ReadRawData()
        {
            MemoryStream ms = new MemoryStream();

            byte[] buf = new byte[10240];
            while (running)
            {
                int resSize = rawStream.Read(buf, 0, buf.Length);

                if (ms.Length + resSize >= bytePerframe)//今回読んだデータで1フレーム分のデータに達したか、上回った場合
                {
                    int needSize = bytePerframe - (int)ms.Length;//1フレームに必要な残りのデータのサイズ
                    int remainSize = (int)ms.Length + resSize - bytePerframe;//余ったデータのサイズ

                    ms.Write(buf, 0, bytePerframe - (int)ms.Length);//1フレームに必要な残りのデータを読む

                    Dispatcher.Invoke(() =>
                    {
                        if (writeableBitmap != null)//データを書き込む
                            writeableBitmap.WritePixels(new Int32Rect(0, 0, imageWidth, imageHeight), ms.ToArray(), 3 * imageWidth, 0);
                    });

                    ms.Close();
                    ms = new MemoryStream();
                    ms.Write(buf, needSize + 1, remainSize);//余ったデータを書き込む
                }
                else
                {
                    ms.Write(buf, 0, resSize);//データを蓄積
                }
            }
        }

        //InputHostを起動し、接続する
        private void StartInputHost()
        {
            string inputInfo = Exec("adb shell getevent -i");//Android端末の入力に関わるデータを取得
            //中からタッチ座標の最大値を抜き取る
            string[] tmp = GetRegexResult(inputInfo, @"ABS[\s\S]*?35.*?max (.*?),[\s\S]*?max (.*?),");
            displayWidth = int.Parse(tmp[0]);
            displayHeight = int.Parse(tmp[1]);

            //ポートの設定
            Exec("adb forward tcp:8081 tcp:8081");
            //アプリのパスを取得
            //余計な文字や改行コードは削除
            string pathToPackage = Exec("adb shell pm path space.siy.screencastsample").Replace("package:", "").Replace("\r\n", "");

            Process process = new Process
            {
                StartInfo =
                 {
                    FileName = "adb",
                    Arguments = $"shell",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                 },
                EnableRaisingEvents = true
            };
            process.Start();
            process.OutputDataReceived += (s, e) =>
            {
                Console.WriteLine(e.Data);//今後なにか処理をするかも
            };
            process.BeginOutputReadLine();
            //Shell権限でInputHostを起動
            process.StandardInput.WriteLine($"sh -c \"CLASSPATH={pathToPackage} /system/bin/app_process /system/bin space.siy.screencastsample.InputHost\"");
            System.Threading.Thread.Sleep(1000);//起動するまで待機
            TcpClient tcp = new TcpClient("127.0.0.1", 8081);//InputHostに接続
            streamToInputHost = tcp.GetStream();
        }

        private void image_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Point p = GetDisplayPosition(e.GetPosition(image));
            byte[] sendByte = Encoding.UTF8.GetBytes($"screen 0 {p.X} {p.Y}\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
            mouseDown = true;
        }

        private void image_MouseMove(object sender, MouseEventArgs e)
        {
            if (mouseDown)
            {
                Point p = GetDisplayPosition(e.GetPosition(image));
                byte[] sendByte = Encoding.UTF8.GetBytes($"screen 2 {p.X} {p.Y}\n");
                streamToInputHost.Write(sendByte, 0, sendByte.Length);
            }
        }

        private void image_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Point p = GetDisplayPosition(e.GetPosition(image));
            byte[] sendByte = Encoding.UTF8.GetBytes($"screen 1 {p.X} {p.Y}\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
            mouseDown = false;
        }

        //マウスの位置を端末のタッチ座標に変換
        private Point GetDisplayPosition(Point p)
        {
            int x = (int)(p.X / image.ActualWidth * displayWidth);
            int y = (int)(p.Y / image.ActualHeight * displayHeight);
            return new Point(x, y);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            running = false;
            byte[] sendByte = Encoding.UTF8.GetBytes($"exit\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
            streamToInputHost.Close();
            rawStream.Close();
        }

        private void Polygon_MouseDown(object sender, MouseButtonEventArgs e)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes($"key 0 4\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
        }

        private void Polygon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes($"key 1 4\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
        }

        private void Ellipse_MouseDown(object sender, MouseButtonEventArgs e)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes($"key 0 3\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
        }

        private void Ellipse_MouseUp(object sender, MouseButtonEventArgs e)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes($"key 1 3\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
        }

        private void Rectangle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes($"key 0 187\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
        }

        private void Rectangle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes($"key 1 187\n");
            streamToInputHost.Write(sendByte, 0, sendByte.Length);
        }

        //コマンド実行して標準出力を返すだけ
        private string Exec(string str)
        {
            Process process = new Process
            {
                StartInfo =
                 {
                    FileName =  "cmd",
                    Arguments = @"/c " + str,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                 },
                EnableRaisingEvents = true
            };
            process.Start();
            string results = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            process.Close();
            return results;
        }

        //正規表現でマッチしたデータを配列で返す
        private string[] GetRegexResult(string src, string pattern)
        {
            Regex regex = new Regex(pattern);
            Match match = regex.Match(src);
            string[] res = new string[match.Groups.Count - 1];
            for (int i = 1; i < match.Groups.Count; i++)
                res[i - 1] = match.Groups[i].Value;
            return res;
        }
    }
}
