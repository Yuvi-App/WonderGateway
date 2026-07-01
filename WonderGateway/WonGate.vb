Imports System.Net
Imports System.Net.Sockets
Imports System.Linq

' The emulated adapter: gateway state + the real work (TCP sockets, DNS, status).
Public Class WonGate
    ' Status constants
    Public Const MWGSTAT_OFF As Byte = 0
    Public Const MWGSTAT_ON As Byte = 2
    Public Const MWGSTAT_PPP As Byte = 3

    Public Const PDCSTAT_OK As Byte = 0
    Public Const PDCSTAT_NOPDC As Byte = 3

    Public Const PPPSTAT_OK As Byte = 1

    Public Const DIALSTAT_OK As Byte = 0

    Public Const FIRST_SOCK As Integer = 0   ' socket ids 0..; Rainbow Islands rejects ids >= 9
    Public Const WONDERGATE_VERSION As Byte = &H10

    Public Cfg As WonConfig
    Public Ver As Byte = WONDERGATE_VERSION
    Public Status As Byte = MWGSTAT_OFF
    Public Dns1 As UInteger
    Public Dns2 As UInteger
    Public PdcReception As Byte = 15
    Public PdcStatus As Byte = PDCSTAT_NOPDC
    Public PppUser As String
    Public PppPass As String
    Public PhoneDialed As String

    ' WonderSwan socket id -> live .NET socket. Ids start at FIRST_SOCK.
    Private ReadOnly _sockets As New Dictionary(Of Byte, Socket)()

    Public Sub New(cfg As WonConfig)
        Me.Cfg = cfg
        ClearState()
    End Sub

    Public Sub ClearState()
        Status = MWGSTAT_OFF
        Dns1 = 0
        Dns2 = 0
        PdcReception = CByte(If(Cfg IsNot Nothing, Cfg.Reception, 15))
        PdcStatus = PDCSTAT_NOPDC
        PppUser = If(Cfg IsNot Nothing, Cfg.PppUser, Nothing)
        PppPass = If(Cfg IsNot Nothing, Cfg.PppPass, Nothing)
        PhoneDialed = Nothing
        CloseAllSockets()
    End Sub

    Private Sub CloseAllSockets()
        For Each s In _sockets.Values
            Try : s.Close() : Catch : End Try
        Next
        _sockets.Clear()
    End Sub

    Public Function PowerOn() As UShort
        If Status = MWGSTAT_OFF Then
            ClearState()
            Status = MWGSTAT_ON
            PdcReception = CByte(If(Cfg IsNot Nothing, Cfg.Reception, 15))
            PdcStatus = PDCSTAT_OK
        End If
        ' Version as two ASCII nibble digits, little-endian word.
        Dim lo As Integer = &H30 + ((Ver >> 4) And &HF)
        Dim hi As Integer = &H30 + (Ver And &HF)
        Return CUShort((lo And &HFF) Or ((hi And &HFF) << 8))
    End Function

    Public Function PowerOff() As Byte
        Status = MWGSTAT_OFF
        Return 0
    End Function

    Public Function SetPppLogin(user As String, pass As String) As Byte
        PppUser = user
        PppPass = pass
        WLog.Dbg($"# Set PPP login: user={If(user, "(none)")} pass={If(pass, "(none)")}")
        Return PPPSTAT_OK
    End Function

    Public Function SetDns(d1 As Byte(), d2 As Byte()) As Byte
        Dns1 = PackBE(d1)
        Dns2 = PackBE(d2)
        WLog.Dbg($"# Set DNS {FmtIp(d1)} / {FmtIp(d2)}")
        Return 1
    End Function

    Public Function Dial(num As String) As Byte
        PhoneDialed = num
        WLog.Dbg($"# Dialing {If(num, "(none)")}")
        Status = MWGSTAT_PPP
        Return DIALSTAT_OK
    End Function

    Public Function Hangup() As Byte
        PhoneDialed = Nothing
        WLog.Dbg("# Hanging up.")
        Status = MWGSTAT_ON
        Return DIALSTAT_OK
    End Function

    ' Resolve a hostname. Returns 1 + canonical name + 4-byte IP, or 0 on failure.
    Public Function GetHost(name As String, ByRef canon As String, ByRef ip As Byte()) As Byte
        canon = Nothing
        ip = Nothing
        If String.IsNullOrEmpty(name) Then Return 0

        Dim target As String = ApplyHostHack(name)
        WLog.Dbg($"# Lookup '{name}' ({target})")

        ' If the target is already an IP, skip DNS.
        Dim direct As IPAddress = Nothing
        If IPAddress.TryParse(target, direct) AndAlso direct.AddressFamily = AddressFamily.InterNetwork Then
            ip = direct.GetAddressBytes()
            canon = name
            WLog.Dbg($"#  -> {FmtIp(ip)}")
            Return 1
        End If

        Try
            Dim he = Dns.GetHostEntry(target)
            Dim a = he.AddressList.FirstOrDefault(Function(x) x.AddressFamily = AddressFamily.InterNetwork)
            If a Is Nothing Then Return 0
            ip = a.GetAddressBytes()
            canon = If(String.IsNullOrEmpty(he.HostName), name, he.HostName)
            WLog.Dbg($"#  -> {canon} {FmtIp(ip)}")
            Return 1
        Catch ex As Exception
            WLog.Dbg($"#  lookup failed: {ex.Message}")
            Return 0
        End Try
    End Function

    Public Function SockNew() As Byte
        Try
            Dim s As New Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            Dim id As Integer = FIRST_SOCK
            While id < 255 AndAlso _sockets.ContainsKey(CByte(id))
                id += 1
            End While
            If id >= 255 Then
                s.Close()
                Return &HFF
            End If
            _sockets(CByte(id)) = s
            WLog.Dbg($"# New socket -> id {id:X2}")
            Return CByte(id)
        Catch ex As Exception
            WLog.Dbg($"# socknew failed: {ex.Message}")
            Return &HFF
        End Try
    End Function

    Public Function SockCon(s As Byte, afam As UShort, data As Byte(), datalen As Integer) As Byte
        Dim sock = GetSock(s)
        If sock Is Nothing Then Return 0

        Select Case afam
            Case 0US, 2US ' AF_UNSPEC / AF_INET
                If datalen < 6 Then Return 0
                ' Wire: [port_lo][port_hi][ip0..3]; port little-endian, ip network order.
                Dim port As Integer = data(0) Or (CInt(data(1)) << 8)
                Dim ipb As Byte() = {data(2), data(3), data(4), data(5)}
                ApplySockHack(ipb, port)
                Try
                    Dim ep As New IPEndPoint(New IPAddress(ipb), port)
                    WLog.Dbg($"# Connecting socket {s:X2} to {ep}")
                    sock.Connect(ep)
                    Return 1
                Catch ex As Exception
                    WLog.Dbg($"# connect failed: {ex.Message}")
                    Return 0
                End Try
            Case Else
                WLog.Dbg($"# Unknown address family {afam} for sockcon")
                Return 0
        End Select
    End Function

    Public Function SockSend(s As Byte, data As Byte(), offset As Integer, len As Integer) As Byte
        Dim sock = GetSock(s)
        If sock Is Nothing Then Return &HFF
        If len <= 0 Then Return 0
        Try
            Dim sent As Integer = sock.Send(data, offset, len, SocketFlags.None)
            Return CByte(sent And &HFF)
        Catch ex As Exception
            WLog.Dbg($"# send failed: {ex.Message}")
            Return &HFF
        End Try
    End Function

    ' Receive up to len bytes. Returns count, or -1 when the connection is closed
    ' (EOF or reset). The dispatcher maps -1 to a 0xFF length byte, which games like
    ' Rainbow Islands require to detect end-of-response (a plain 0 makes them spin).
    Public Function SockRecv(s As Byte, buf As Byte(), len As Integer) As Integer
        Dim sock = GetSock(s)
        If sock Is Nothing Then Return -1
        If len <= 0 Then Return 0
        Try
            Dim n As Integer = sock.Receive(buf, 0, len, SocketFlags.None)
            If n <= 0 Then Return -1   ' orderly close (EOF)
            Return n
        Catch ex As Exception
            WLog.Dbg($"# recv closed: {ex.Message}")
            Return -1   ' reset/error
        End Try
    End Function

    ' 0x1106 "shutdown socket". how: 0=recv, 1=send, else both.
    Public Function SockShutdown(s As Byte, how As Byte) As Byte
        Dim sock = GetSock(s)
        If sock Is Nothing Then Return 0
        Try
            Dim sd As SocketShutdown
            Select Case how
                Case 0 : sd = SocketShutdown.Receive
                Case 1 : sd = SocketShutdown.Send
                Case Else : sd = SocketShutdown.Both
            End Select
            sock.Shutdown(sd)
            WLog.Dbg($"# Shutdown socket {s:X2} how={how}")
        Catch ex As Exception
            WLog.Dbg($"# shutdown failed: {ex.Message}")
        End Try
        Return 1
    End Function

    Public Function SockDel(s As Byte) As Byte
        Dim sock = GetSock(s)
        If sock Is Nothing Then Return 0
        Try : sock.Close() : Catch : End Try
        _sockets.Remove(s)
        WLog.Dbg($"# Closed socket {s:X2}")
        Return 1
    End Function

    Private Function GetSock(s As Byte) As Socket
        Dim sock As Socket = Nothing
        _sockets.TryGetValue(s, sock)
        Return sock
    End Function

    ' Config-driven server redirection. An entry keyed "*.suffix" matches any
    ' host ending in ".suffix" (and the bare "suffix"), so e.g. "*.channel.or.jp"
    ' catches every game server without listing each subdomain.
    Private Function ApplyHostHack(name As String) As String
        If Cfg IsNot Nothing Then
            For Each h In Cfg.HostHacks
                If HostMatches(h.InName, name) Then Return h.OutName
            Next
        End If
        Return name
    End Function

    Private Shared Function HostMatches(pattern As String, name As String) As Boolean
        If pattern Is Nothing OrElse name Is Nothing Then Return False
        If pattern.StartsWith("*.") Then
            Dim suffix As String = pattern.Substring(1) ' ".channel.or.jp"
            Return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(name, pattern.Substring(2), StringComparison.OrdinalIgnoreCase)
        End If
        Return String.Equals(pattern, name, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Sub ApplySockHack(ip As Byte(), ByRef port As Integer)
        If Cfg Is Nothing Then Return
        For Each h In Cfg.SockHacks
            If Not h.IpMatches(ip) Then Continue For
            If h.InPort <> 0 AndAlso h.InPort <> port Then Continue For
            If h.InPort = port Then port = h.OutPort
            h.CopyOutIp(ip)
            Return
        Next
    End Sub

    Private Shared Function PackBE(d As Byte()) As UInteger
        If d Is Nothing OrElse d.Length < 4 Then Return 0
        Return (CUInt(d(0)) << 24) Or (CUInt(d(1)) << 16) Or (CUInt(d(2)) << 8) Or CUInt(d(3))
    End Function

    Private Shared Function FmtIp(d As Byte()) As String
        If d Is Nothing OrElse d.Length < 4 Then Return "?.?.?.?"
        Return $"{d(0)}.{d(1)}.{d(2)}.{d(3)}"
    End Function
End Class
