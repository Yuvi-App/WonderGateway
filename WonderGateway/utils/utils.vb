Imports System.IO
Imports System.Net

' Hostname redirect: in_name resolves to out_name.
Public Class HostHack
    Public InName As String
    Public OutName As String
End Class

' Socket redirect: connections to in_ip[:in_port] go to out_ip[:out_port].
Public Class SockHack
    Public InIp As Byte()    ' a.b.c.d
    Public InPort As Integer ' 0 = any port
    Public OutIp As Byte()
    Public OutPort As Integer

    Public Function IpMatches(ip As Byte()) As Boolean
        If InIp Is Nothing OrElse ip Is Nothing OrElse ip.Length < 4 Then Return False
        Return InIp(0) = ip(0) AndAlso InIp(1) = ip(1) AndAlso InIp(2) = ip(2) AndAlso InIp(3) = ip(3)
    End Function

    Public Sub CopyOutIp(ip As Byte())
        ip(0) = OutIp(0) : ip(1) = OutIp(1) : ip(2) = OutIp(2) : ip(3) = OutIp(3)
    End Sub
End Class

' INI config. Supports [wondergateway]/[pdc]/[ppp]/[hostname_hack]/[socket_hack].
Public Class WonConfig
    Public Transport As String = "stdio"   ' "stdio" (Mednafen) or "serial"
    Public ComPort As String = "COM1"
    Public Baud As Integer = 9600          ' serial only: 9600 or 38400
    Public Reception As Integer = 15
    Public DialNumber As String = ""
    Public PppUser As String = ""
    Public PppPass As String = ""
    Public DebugLog As Boolean = True
    Public LogFile As String = ""
    Public HostHacks As New List(Of HostHack)()
    Public SockHacks As New List(Of SockHack)()

    Public Shared Function Load(path As String) As WonConfig
        Dim cfg As New WonConfig()
        If String.IsNullOrEmpty(path) OrElse Not File.Exists(path) Then Return cfg

        Dim section As String = ""
        For Each raw In File.ReadAllLines(path)
            Dim line = raw.Trim()
            If line = "" OrElse line.StartsWith(";") OrElse line.StartsWith("#") Then Continue For

            If line.StartsWith("[") AndAlso line.EndsWith("]") Then
                section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant()
                Continue For
            End If

            Dim eq = line.IndexOf("="c)
            If eq < 0 Then Continue For
            Dim key = line.Substring(0, eq).Trim()
            Dim val = line.Substring(eq + 1).Trim()

            Select Case section
                Case "wondergateway", ""
                    Select Case key.ToLowerInvariant()
                        Case "transport" : cfg.Transport = val.ToLowerInvariant()
                        Case "comport" : cfg.ComPort = val
                        Case "baud" : Integer.TryParse(val, cfg.Baud)
                        Case "reception" : Integer.TryParse(val, cfg.Reception)
                        Case "dialnumber" : cfg.DialNumber = val
                        Case "debug" : cfg.DebugLog = ParseBool(val)
                        Case "logfile" : cfg.LogFile = val
                        Case "username" : cfg.PppUser = val   ' legacy
                        Case "password" : cfg.PppPass = val   ' legacy
                    End Select
                Case "pdc"
                    If key.ToLowerInvariant() = "reception" Then Integer.TryParse(val, cfg.Reception)
                Case "ppp"
                    Select Case key.ToLowerInvariant()
                        Case "user" : cfg.PppUser = val
                        Case "pass" : cfg.PppPass = val
                    End Select
                Case "hostname_hack"
                    cfg.HostHacks.Add(New HostHack With {.InName = key, .OutName = val})
                Case "socket_hack"
                    Dim sh = ParseSockHack(key, val)
                    If sh IsNot Nothing Then cfg.SockHacks.Add(sh)
            End Select
        Next
        Return cfg
    End Function

    Private Shared Function ParseBool(v As String) As Boolean
        Select Case v.Trim().ToLowerInvariant()
            Case "1", "true", "yes", "on" : Return True
            Case Else : Return False
        End Select
    End Function

    Private Shared Function ParseSockHack(name As String, value As String) As SockHack
        Dim inIp(3) As Byte, outIp(3) As Byte
        Dim inPort As Integer, outPort As Integer
        If Not ParseIpPort(name, inIp, inPort) Then Return Nothing
        If Not ParseIpPort(value, outIp, outPort) Then Return Nothing
        Return New SockHack With {.InIp = inIp, .InPort = inPort, .OutIp = outIp, .OutPort = outPort}
    End Function

    Private Shared Function ParseIpPort(s As String, ip As Byte(), ByRef port As Integer) As Boolean
        port = 0
        Dim hostpart = s.Trim()
        Dim colon = hostpart.IndexOf(":"c)
        If colon >= 0 Then
            Integer.TryParse(hostpart.Substring(colon + 1), port)
            hostpart = hostpart.Substring(0, colon)
        End If
        Dim addr As IPAddress = Nothing
        If Not IPAddress.TryParse(hostpart.Trim(), addr) Then Return False
        Dim b = addr.GetAddressBytes()
        If b.Length <> 4 Then Return False
        Array.Copy(b, ip, 4)
        Return True
    End Function
End Class
