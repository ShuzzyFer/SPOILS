// See https://aka.ms/new-console-template for more information

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class FileTransferTcpServer
{
    private TcpListener _listener;
    private string _fileDirectory = "./files"; // Директория для хранения файлов

    public async Task StartAsync(int port)
    {
        // Создаем директорию для файлов, если её нет
        Directory.CreateDirectory(_fileDirectory);

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        Console.WriteLine("Server started. Waiting for connections...");

        while (true)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync();
            Console.WriteLine("Client connected.");

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            StringBuilder messageBuilder = new StringBuilder(); // Для сбора частичных данных
            byte[] buffer = new byte[1024];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                // Добавляем прочитанные данные в StringBuilder
                string partialMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuilder.Append(partialMessage);

                // Ищем символы конца строки (\r\n)
                int endOfLineIndex = messageBuilder.ToString().IndexOfAny(new char[] { '\r', '\n' });

                while (endOfLineIndex >= 0)
                {
                    // Получаем полную команду до символа конца строки
                    string fullCommand = messageBuilder.ToString(0, endOfLineIndex).Trim();
                    messageBuilder.Remove(0, endOfLineIndex + 1); // Удаляем обработанную команду

                    if (!string.IsNullOrEmpty(fullCommand))
                    {
                        // Проверяем, является ли команда UPLOAD
                        if (fullCommand.StartsWith("UPLOAD", StringComparison.OrdinalIgnoreCase))
                        {
                            string filename = fullCommand.Split(' ')[1].Trim();
                            await HandleUpload(stream, filename);

                            // После завершения загрузки файла, очищаем буфер
                            messageBuilder.Clear();
                            //continue; // Пропускаем остальную логику для этой итерации
                        }
                        else
                        {

                            // Обработка других команд
                            string response = ProcessCommand(fullCommand, stream);

                            if (!string.IsNullOrEmpty(response))
                            {
                                byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\r\n");
                                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                            }

                            // Если команда CLOSE/EXIT/QUIT, закрываем соединение
                            if (fullCommand.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) ||
                                fullCommand.Equals("EXIT", StringComparison.OrdinalIgnoreCase) ||
                                fullCommand.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                            {
                                return;
                            }
                        }
                    }

                    // Ищем следующий символ конца строки
                    endOfLineIndex = messageBuilder.ToString().IndexOfAny(new char[] { '\r', '\n' });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client: {ex.Message}");
        }
        finally
        {
            client.Close();
            Console.WriteLine("Client disconnected.");
        }
    }

    private string ProcessCommand(string command, NetworkStream stream)
    {
        if (command.StartsWith("DOWNLOAD", StringComparison.OrdinalIgnoreCase))
        {
            string filename = command.Split(' ')[1].Trim();
            _ = HandleDownload(stream, filename); // Асинхронная обработка скачивания
            return ""; // Нет текстового ответа для этой команды
        }

        // Обработка других команд...
        else if (command.StartsWith("UPLOAD", StringComparison.OrdinalIgnoreCase))
        {
            string filename = command.Split(' ')[1].Trim();
            _ = HandleUpload(stream, filename); // Асинхронная обработка загрузки
            return "";
        }
        else if (command.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("EXIT", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection closed.";
        }
        else if (command.Equals("TIME", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else if (command.StartsWith("ECHO", StringComparison.OrdinalIgnoreCase))
        {
            return command.Substring(5).Trim();
        }
        else
        {
            return "Unknown command.";
        }
    }

    private async Task HandleUpload(NetworkStream stream, string filename)
    {
        Console.WriteLine($"Starting upload of {filename}...");

        // Создаем директорию, если её нет
        Directory.CreateDirectory(_fileDirectory);


        long totalBytesReceived = 0; // Счетчик принятых байтов
        DateTime startTime = DateTime.Now; // Время начала передачи

        string fullPath = Path.Combine(_fileDirectory, filename);

        using (FileStream fileStream = new FileStream(
                   fullPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   1048576, // Размер буфера 1 MB
                   FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[1048576]; // Размер буфера 1 MB
            List<byte> remainingData = new List<byte>(); // Буфер для хранения неполных данных

            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    throw new IOException("Connection closed unexpectedly.");
                }

                // Добавляем текущие данные в буфер
                remainingData.AddRange(buffer.Take(bytesRead));

                // Проверяем наличие маркера <EOF>
                int eofIndex = FindEndMarker(remainingData);
                if (eofIndex >= 0)
                {
                    // Сохраняем данные до маркера
                    byte[] dataBeforeEof = remainingData.Take(eofIndex).ToArray();
                    if (dataBeforeEof.Length > 0)
                    {
                        await fileStream.WriteAsync(dataBeforeEof, 0, dataBeforeEof.Length);
                        fileStream.Flush();
                        totalBytesReceived += dataBeforeEof.Length;
                    }

                    Console.WriteLine("End marker detected. Finishing upload.");

                    // Очищаем буфер после завершения передачи
                    remainingData.Clear();

                    break; // Завершение передачи
                }

                // Сохраняем все данные, кроме маркера
                byte[] dataToSave = remainingData.ToArray();
                if (dataToSave.Length > 0)
                {
                    await fileStream.WriteAsync(dataToSave, 0, dataToSave.Length);
                    fileStream.Flush();
                    totalBytesReceived += dataToSave.Length;
                }

                remainingData.Clear(); // Очищаем буфер

                // Логирование прогресса каждые 10 MB
                if (totalBytesReceived % (10 * 1024 * 1024) == 0) // 10 MB
                {
                    Console.WriteLine($"Uploaded {totalBytesReceived / (1024 * 1024)} MB...");
                }
            }

            // Вычисляем время передачи и скорость
            TimeSpan duration = DateTime.Now - startTime;
            double speedMBps = (totalBytesReceived / (1024.0 * 1024.0)) / duration.TotalSeconds; // Скорость в МБ/с

            Console.WriteLine("File uploaded successfully.");
            Console.WriteLine($"Total size: {totalBytesReceived} bytes");
            Console.WriteLine($"Duration: {duration.TotalSeconds:F2} seconds");
            Console.WriteLine($"Speed: {speedMBps:F2} MB/s");

            // Очищаем буфер полностью
            remainingData.Clear();
        }
    }

// Метод для поиска маркера окончания передачи
    private int FindEndMarker(List<byte> buffer)
    {
        byte[] endMarker = Encoding.UTF8.GetBytes("<EOF>"); // Маркер конца передачи
        if (buffer.Count < endMarker.Length) return -1;

        for (int i = 0; i <= buffer.Count - endMarker.Length; i++)
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

    private async Task HandleDownload(NetworkStream stream, string filename)
    {
        Console.WriteLine($"Starting download of {filename}...");

        // Проверяем существование файла
        string fullPath = Path.Combine(_fileDirectory, filename);
        if (!File.Exists(fullPath))
        {
            byte[] responseBytes = Encoding.UTF8.GetBytes("File not found.\r\n");
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
            return;
        }

        long totalBytesSent = 0; // Счетчик отправленных байтов
        DateTime startTime = DateTime.Now; // Время начала передачи

        try
        {
            using (FileStream fileStream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None,
                       1048576, // Размер буфера 1 MB
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[1048576]; // Размер буфера 1 MB

                while (true)
                {
                    int bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // Конец файла

                    // Отправляем только прочитанные байты
                    await stream.WriteAsync(buffer, 0, bytesRead);

                    totalBytesSent += bytesRead;

                    // Логирование прогресса каждые 10 MB
                    if (totalBytesSent % (10 * 1024 * 1024) == 0) // 10 MB
                    {
                        Console.WriteLine($"Downloaded {totalBytesSent / (1024 * 1024)} MB...");
                    }
                }

                // Отправляем специальный маркер окончания передачи
                byte[] endMarker = Encoding.UTF8.GetBytes("<EOF>"); // Маркер конца передачи
                await stream.WriteAsync(endMarker, 0, endMarker.Length);

                // Вычисляем время передачи и скорость
                TimeSpan duration = DateTime.Now - startTime;
                double speedMBps = (totalBytesSent / (1024.0 * 1024.0)) / duration.TotalSeconds; // Скорость в МБ/с

                Console.WriteLine("File downloaded successfully.");
                Console.WriteLine($"Total size: {totalBytesSent} bytes");
                Console.WriteLine($"Duration: {duration.TotalSeconds:F2} seconds");
                Console.WriteLine($"Speed: {speedMBps:F2} MB/s");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
        }
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        FileTransferTcpServer server = new FileTransferTcpServer();
        await server.StartAsync(5000);
    }
}