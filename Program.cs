// See https://aka.ms/new-console-template for more information

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class TcpClientApp
{
    private TcpClient _client;
    private NetworkStream _stream;

    private bool check = false;

    public async Task ConnectAsync(string serverIp, int port)
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(serverIp, port);
            _stream = _client.GetStream();

            Console.WriteLine("Connected to server. Type 'HELP' for available commands.");

            // Запускаем задачу для чтения данных с сервера
            

            // Запускаем цикл для отправки команд
            while (true)
            {
                Console.Write("> ");
                string command = Console.ReadLine();

                if (string.IsNullOrEmpty(command))
                    continue;
                if (command.Equals("EXIT", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await SendCommandAsync(command);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    private async Task SendCommandAsync(string command)
    {
        try
        {
            byte[] commandBytes = Encoding.UTF8.GetBytes(command + "\r\n");
            await _stream.WriteAsync(commandBytes, 0, commandBytes.Length);

            if (command.StartsWith("DOWNLOAD", StringComparison.OrdinalIgnoreCase))
            {
                string filename = command.Split(' ')[1].Trim();
                await DownloadFileAsync(filename);
            }
            else if (command.StartsWith("UPLOAD", StringComparison.OrdinalIgnoreCase))
            {
                string filename = command.Split(' ')[1].Trim();
                await UploadFileAsync(filename);
            }
            else if (command.Equals("TIME", StringComparison.OrdinalIgnoreCase))
            {
                await ReadTimeResponse();
            }
            else if (command.StartsWith("ECHO", StringComparison.OrdinalIgnoreCase))
            {
                await ReadEchoResponse();
            }
            else if (command.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) ||
                     command.Equals("EXIT", StringComparison.OrdinalIgnoreCase) ||
                     command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Closing connection...");
                _stream.Close();
            }
            else
            {
                Console.WriteLine("Unknown command.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending command: {ex.Message}");
        }
    }
    
    private async Task ReadTimeResponse()
    {
        byte[] buffer = new byte[1024];
        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

        if (bytesRead > 0)
        {
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            Console.WriteLine($"Server: {response}");
        }
        else
        {
            Console.WriteLine("Failed to receive server time.");
        }
    }
    
    private async Task ReadEchoResponse()
    {
        byte[] buffer = new byte[1024];
        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

        if (bytesRead > 0)
        {
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            Console.WriteLine($"Server: {response}");
        }
        else
        {
            Console.WriteLine("Failed to receive echo response.");
        }
    }

    private async Task UploadFileAsync(string filename, int offset = 0)
    {
        if (!File.Exists(filename))
        {
            Console.WriteLine("File not found.");
            return;
        }

        Console.WriteLine($"Uploading file: {filename}");

        long totalBytesSent = 0; // Счетчик отправленных байтов
        DateTime startTime = DateTime.Now; // Время начала передачи

        try
        {
            using (FileStream fileStream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.None,
                       1048576, FileOptions.Asynchronous))
            {
                // Пропускаем указанный offset
                fileStream.Position = offset;

                byte[] buffer = new byte[1048576]; // Размер буфера 1 MB

                while (true)
                {
                    int bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // Конец файла

                    await _stream.WriteAsync(buffer, 0, bytesRead);

                    totalBytesSent += bytesRead;

                    // Логирование прогресса каждые 10 MB
                    if (totalBytesSent % (10 * 1024 * 1024) == 0) // 10 MB
                    {
                        Console.WriteLine($"Uploaded {totalBytesSent / (1024 * 1024)} MB...");
                    }
                }

                // Отправляем специальный маркер окончания передачи
                byte[] endMarker = Encoding.UTF8.GetBytes("<EOF>"); // Маркер конца передачи
                await _stream.WriteAsync(endMarker, 0, endMarker.Length);

                // Вычисляем время передачи и скорость
                TimeSpan duration = DateTime.Now - startTime;
                double speedMBps = (totalBytesSent / (1024.0 * 1024.0)) / duration.TotalSeconds; // Скорость в МБ/с

                Console.WriteLine("File uploaded successfully.");
                Console.WriteLine($"Total size: {totalBytesSent} bytes");
                Console.WriteLine($"Duration: {duration.TotalSeconds:F2} seconds");
                Console.WriteLine($"Speed: {speedMBps:F2} MB/s");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error uploading file: {ex.Message}");
        }
    }
    
    private async Task<long> ReadFileSizeFromServer()
    {
        byte[] buffer = new byte[1024];
        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

        if (bytesRead == 0)
        {
            throw new IOException("Connection closed unexpectedly.");
        }

        string fileSizeString = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
        if (long.TryParse(fileSizeString, out long fileSize))
        {
            return fileSize;
        }

        return 0; // Файл не найден
    }

   private async Task DownloadFileAsync(string filename)
{
    Console.WriteLine($"Downloading file: {filename}");

    string fullPath = Path.Combine(Directory.GetCurrentDirectory(), filename);

    try
    {
        // Шаг 1: Читаем размер файла как метаданные
        long fileSize = await ReadFileSizeFromServer();
        if (fileSize == 0)
        {
            Console.WriteLine($"File {filename} not found on server.");
            return;
        }

        // Шаг 2: Определяем текущий offset
        long currentOffset = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;

        // Шаг 3: Отправляем offset серверу
        byte[] offsetBytes = Encoding.UTF8.GetBytes($"{currentOffset}\r\n");
        await _stream.WriteAsync(offsetBytes, 0, offsetBytes.Length);

        // Шаг 4: Начинаем скачивание файла
        long totalBytesReceived = currentOffset; // Используем currentOffset как начальную точку
        DateTime startTime = DateTime.Now; // Время начала передачи

        using (FileStream fileStream = new FileStream(
            fullPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            1048576, // Размер буфера 1 MB
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            // Устанавливаем позицию в файле согласно offset
            fileStream.Position = currentOffset;

            byte[] buffer = new byte[1048576]; // Размер буфера 1 MB

            while (true)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    throw new IOException("Connection closed unexpectedly.");
                }

                // Проверяем наличие маркера <EOF>
                int eofIndex = FindEndMarker(buffer, bytesRead);
                if (eofIndex >= 0)
                {
                    // Сохраняем данные до маркера
                    byte[] dataBeforeEof = buffer.Take(eofIndex).ToArray();
                    if (dataBeforeEof.Length > 0)
                    {
                        await fileStream.WriteAsync(dataBeforeEof, 0, dataBeforeEof.Length);
                        fileStream.Flush();
                        totalBytesReceived += dataBeforeEof.Length;
                    }

                    Console.WriteLine("End marker detected. Finishing download.");

                    break; // Завершение передачи
                }

                // Записываем все прочитанные байты в файл
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                fileStream.Flush();

                totalBytesReceived += bytesRead;

                // Логирование прогресса каждые 10 MB
                if (totalBytesReceived % (10 * 1024 * 1024) == 0) // 10 MB
                {
                    Console.WriteLine($"Downloaded {totalBytesReceived / (1024 * 1024)} MB...");
                }
            }

            // Проверяем, что все данные были получены
            if (totalBytesReceived != fileSize)
            {
                throw new IOException($"Incomplete file transfer. Expected {fileSize} bytes, but received {totalBytesReceived} bytes.");
            }

            // Вычисляем время передачи и скорость
            TimeSpan duration = DateTime.Now - startTime;
            double speedMBps = (totalBytesReceived / (1024.0 * 1024.0)) / duration.TotalSeconds; // Скорость в МБ/с

            Console.WriteLine("File downloaded successfully.");
            Console.WriteLine($"Total size: {totalBytesReceived} bytes");
            Console.WriteLine($"Duration: {duration.TotalSeconds:F2} seconds");
            Console.WriteLine($"Speed: {speedMBps:F2} MB/s");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error downloading file: {ex.Message}");
    }
}

// Метод для поиска маркера <EOF> в текущем буфере
private int FindEndMarker(byte[] buffer, int bytesRead)
{
    byte[] endMarker = Encoding.UTF8.GetBytes("<EOF>"); // Маркер конца передачи
    if (bytesRead < endMarker.Length) return -1;

    for (int i = 0; i <= bytesRead - endMarker.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < endMarker.Length; j++)
        {
            if (buffer[i + j] != endMarker[j])
            {
                match = false;
                break;
            }
        }

        if (match)
        {
            return i; // Возвращаем индекс начала маркера
        }
    }

    return -1; // Маркер не найден
}

    
    private bool isDownloading = false; // Флаг для индикации скачивания

    private void Disconnect()
    {
        _stream?.Close();
        _client?.Close();
        Console.WriteLine("Disconnected from server.");
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        Console.Write("Enter server IP: ");
        string serverIp = "192.168.50.174";

        Console.Write("Enter server port: ");
        if (!int.TryParse(Console.ReadLine(), out int port))
        {
            port = 5000; // Default port
        }

        TcpClientApp client = new TcpClientApp();
        await client.ConnectAsync(serverIp, port);
    }
}