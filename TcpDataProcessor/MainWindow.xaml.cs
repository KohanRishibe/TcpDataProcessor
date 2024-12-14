using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Windows;

namespace TcpDataProcessor
{
    public partial class MainWindow : Window
    {
        private List<InputServer> _inputServers;
        private TcpListener _outputServer;
        private List<TcpClient> _outputClients = new List<TcpClient>();
        private Dictionary<string, List<string>> _serverData = new Dictionary<string, List<string>>(); 

        public MainWindow()
        {
            InitializeComponent();
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conf.json");
                var configJson = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Config>(configJson);

                _inputServers = config.InputServers;
                _outputServer = new TcpListener(IPAddress.Any, config.OutputServer.Port);

                Log("Конфигурация загружена.");
            }
            catch (Exception ex)
            {
                Log($"Ошибка загрузки конфигурации: {ex.Message}");
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Log("Запуск обработки данных...");

            _outputServer.Start();
            Log($"Выходной сервер запущен на порте {_outputServer.LocalEndpoint}");
            _ = AcceptOutputClientsAsync();

            foreach (var server in _inputServers)
            {
                _ = ConnectToServerAsync(server);
            }
        }

        private async Task AcceptOutputClientsAsync()
        {
            while (true)
            {
                try
                {
                    var client = await _outputServer.AcceptTcpClientAsync();
                    _outputClients.Add(client);
                    Log($"Клиент подключился к выходному серверу.");
                }
                catch (Exception ex)
                {
                    Log($"Ошибка при подключении клиента к выходному серверу: {ex.Message}");
                }
            }
        }

        private int _receivedServersCount = 0;
        private int _totalServersCount = 4;          

        private async Task ConnectToServerAsync(InputServer server)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(server.Host, server.Port);
                Log($"Подключен к серверу {server.Host}:{server.Port}");

                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);

                while (client.Connected)
                {
                    var data = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        Log($"Получено от {server.Host}:{server.Port} -> {data}");
                        await HandleIncomingDataAsync(server, data);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка подключения к серверу {server.Host}:{server.Port}: {ex.Message}");
            }
        }

        private async Task HandleIncomingDataAsync(InputServer server, string data)
        {
            try
            {
                Log($"Обрабатываем данные от {server.Host}:{server.Port}: {data}");

                if (!data.StartsWith("#90") || !data.EndsWith("#91"))
                {
                    Log($"Некорректный формат данных от {server.Host}:{server.Port}");
                    return;
                }

                var start = data.IndexOf("#27") + 3;
                var end = data.LastIndexOf("#91");
                var content = data.Substring(start, end - start);
                var parts = content.Split(';');

                if (parts.Length != 2)
                {
                    Log($"Ошибка разбора данных от {server.Host}:{server.Port}");
                    return;
                }

                var data1 = parts[0];
                var data2 = parts[1];

                // Сохранение данных от сервера
                if (!_serverData.ContainsKey(server.Host))
                {
                    _serverData[server.Host] = new List<string>();
                }

                var combinedData = $"{data1};{data2}";
                _serverData[server.Host].Add(combinedData); // Добавляем данные для текущего сервера
                Log($"Данные от {server.Host} сохранены: {combinedData}");
                _receivedServersCount++;
                Log($"Получено данных от {_receivedServersCount} серверов.");

                if (_receivedServersCount == _totalServersCount)
                {
                    Log("Все данные получены от серверов.");
                    await CompareAndSendResultsAsync();
                }
                else
                {
                    Log("Не все данные получены от серверов.");
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка обработки данных от {server.Host}:{server.Port}: {ex.Message}");
            }
        }



        private async Task CompareAndSendResultsAsync()
        {
            try
            {
                Log($"Проверяем данные от {_receivedServersCount} серверов.");
                if (_receivedServersCount != _totalServersCount)
                {
                    Log($"Не все данные получены. Получено: {_receivedServersCount}, ожидаемо: {_totalServersCount}");
                    return;
                }

                foreach (var server in _serverData)
                {
                    Log($"Данные от сервера {server.Key}: {string.Join(", ", server.Value)}");
                }

                var comparisonResult = CompareData();
                var resultMessage = $"#90#010102#27{comparisonResult}#91";
                await SendToOutputClientsAsync(resultMessage);

                Log($"Результат отправлен: {resultMessage}");
            }
            catch (Exception ex)
            {
                Log($"Ошибка при сравнении данных: {ex.Message}");
            }
        }
        private string CompareData()
        {
            var firstServerData = _serverData.Values.FirstOrDefault(); 
            if (firstServerData == null || !firstServerData.Any())
            {
                Log("Нет данных для сравнения.");
                return "NoRead";
            }
            foreach (var serverData in _serverData.Values.Skip(1))
            {
                if (!serverData.SequenceEqual(firstServerData))
                {
                    Log("Данные от серверов не совпадают.");
                    return "NoRead"; 
                }
            }
            Log("Все данные совпадают.");
            return firstServerData.First();  
        }



        private async Task SendToOutputClientsAsync(string result)
        {
            foreach (var client in _outputClients.ToList())
            {
                if (client.Connected)
                {
                    var stream = client.GetStream();
                    var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    await writer.WriteLineAsync(result);
                    Log($"Отправлено на клиент: {result}");
                }
                else
                {
                    _outputClients.Remove(client);
                    Log("Удален отключенный клиент.");
                }
            }
        }


        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        public class Config
        {
            public List<InputServer> InputServers { get; set; }
            public OutputServer OutputServer { get; set; }
        }

        public class InputServer
        {
            public string Host { get; set; }
            public int Port { get; set; }
        }

        public class OutputServer
        {
            public int Port { get; set; }
        }
    }
}
