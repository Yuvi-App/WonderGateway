Imports System.IO
Imports System.Linq

' Logger: writes to stderr (stdout carries the protocol) and optionally a file.
Public Module WLog
    Public DebugEnabled As Boolean = True
    Private _file As IO.StreamWriter
    Private ReadOnly _lock As New Object()

    Public Sub OpenLogFile(path As String)
        If String.IsNullOrWhiteSpace(path) Then Return
        Try
            If Not IO.Path.IsPathRooted(path) Then
                path = IO.Path.Combine(AppContext.BaseDirectory, path)
            End If
            _file = New IO.StreamWriter(path, append:=True) With {.AutoFlush = True}
            WriteLine($"===== WonderGateway log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====")
        Catch ex As Exception
            Console.Error.WriteLine($"Could not open log file '{path}': {ex.Message}")
        End Try
    End Sub

    Private Sub WriteLine(msg As String)
        Dim line = $"{DateTime.Now:HH:mm:ss.fff} {msg}"
        SyncLock _lock
            Console.Error.WriteLine(line)
            If _file IsNot Nothing Then _file.WriteLine(line)
        End SyncLock
    End Sub

    Public Sub Dbg(msg As String)
        If DebugEnabled Then WriteLine(msg)
    End Sub

    Public Sub Info(msg As String)
        WriteLine(msg)
    End Sub
End Module

Module Program

    Sub Main(args As String())
        If args IsNot Nothing AndAlso args.Any(Function(a) a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)) Then
            Environment.Exit(RunSelfTest())
            Return
        End If

        Dim cfgPath As String = FindConfig(args)
        Dim cfg As WonConfig = WonConfig.Load(cfgPath)
        WLog.DebugEnabled = cfg.DebugLog
        WLog.OpenLogFile(cfg.LogFile)

        WLog.Info("WonderGateway - MobileWonderGate emulator")
        WLog.Info($"Config: {If(cfgPath, "(defaults)")}   Transport: {cfg.Transport}")
        For Each h In cfg.HostHacks
            WLog.Info($"  hostname hack: {h.InName} -> {h.OutName}")
        Next

        Dim transport As IByteTransport
        Try
            If cfg.Transport = "serial" Then
                transport = New SerialTransport(cfg.ComPort, cfg.Baud)
                WLog.Info($"Listening on serial {cfg.ComPort} ({cfg.Baud}/8-N-1)")
            Else
                transport = New StdioTransport()
                WLog.Info("Listening on stdin/stdout (Mednafen excomm mode)")
            End If
        Catch ex As Exception
            WLog.Info($"Failed to open transport: {ex.Message}")
            Environment.Exit(1)
            Return
        End Try

        Dim gate As New WonGate(cfg)

        ' Main loop: recv -> handle -> reply.
        Try
            While True
                Dim cmd = WonGateCmd.Recv(transport)
                If cmd Is Nothing Then Exit While
                WLog.Dbg($"<= Recv Type:Cmd={cmd.Type:X2}:{cmd.Cmd:X2} Size={cmd.Size:X2} {cmd.HexParams()}")
                Dim repl = Dispatcher.Handle(gate, cmd)
                If repl IsNot Nothing Then repl.Send(transport)
            End While
        Catch ex As Exception
            WLog.Info($"Fatal: {ex}")
        Finally
            transport.Close()
        End Try

        WLog.Info("WonderGateway exiting.")
    End Sub

    ' Config file: argv[0], else next to the exe, else CWD.
    Private Function FindConfig(args As String()) As String
        If args IsNot Nothing AndAlso args.Length > 0 AndAlso File.Exists(args(0)) Then Return args(0)
        Dim names = {"wondergateway.ini", "wonfence.ini"}
        Dim dirs = {AppContext.BaseDirectory, Directory.GetCurrentDirectory()}
        For Each d In dirs
            For Each n In names
                Dim p = Path.Combine(d, n)
                If File.Exists(p) Then Return p
            Next
        Next
        Return Nothing
    End Function

    ' Self test - runs the dispatcher over canned requests, no hardware needed.
    Private Function RunSelfTest() As Integer
        Dim gate As New WonGate(New WonConfig())
        Dim ok As Boolean = True

        ok = Check(gate, "poweron", New Byte() {&H1, &H0, &H1, &H2}, New Byte() {&H1, &H0, &H3, &H2, &H31, &H30}) And ok
        ok = Check(gate, "get_status", New Byte() {&H2, &H0, &H2, &H1, &H3}, New Byte() {&H2, &H0, &H2, &H2, &H2}) And ok
        ok = Check(gate, "check_pdc", New Byte() {&H1, &H0, &H1, &H0}, New Byte() {&H1, &H0, &H3, &H0, &H0, &HF}) And ok
        ok = Check(gate, "poweroff_badconf", New Byte() {&HF, &H0, &H1, &HFF}, New Byte() {}) And ok

        WLog.Info(If(ok, "SELFTEST: ALL PASS", "SELFTEST: FAILURES"))
        Return If(ok, 0, 1)
    End Function

    Private Function Check(gate As WonGate, name As String, input As Byte(), expect As Byte()) As Boolean
        Dim mt As New MemoryTransport(input)
        Dim cmd = WonGateCmd.Recv(mt)
        If cmd IsNot Nothing Then
            Dim repl = Dispatcher.Handle(gate, cmd)
            If repl IsNot Nothing Then repl.Send(mt)
        End If
        Dim got = mt.Output.ToArray()
        Dim pass = got.Length = expect.Length AndAlso got.SequenceEqual(expect)
        WLog.Info($"[{If(pass, "PASS", "FAIL")}] {name}: got [{Hex(got)}] expect [{Hex(expect)}]")
        Return pass
    End Function

    Private Function Hex(b As Byte()) As String
        Return String.Join(" ", b.Select(Function(x) x.ToString("X2")))
    End Function
End Module
