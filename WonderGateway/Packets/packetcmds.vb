Imports System.Text

' Command dispatch + per-command handlers, keyed on (type << 8) | cmd.
' A handler fills the reply; returning Nothing means "send no reply".
Public Module Dispatcher

    Public Function Handle(gate As WonGate, cmd As WonGateCmd) As WonGateCmd
        Dim id As Integer = (CInt(cmd.Type) << 8) Or cmd.Cmd
        Dim repl As New WonGateCmd() With {.Type = cmd.Type, .Zero = 0, .Cmd = cmd.Cmd}

        Select Case id
            Case &H102 : Return Cmd_PowerOn(gate, cmd, repl)
            Case &HFFF : Return Cmd_PowerOff(gate, cmd, repl)
            Case &H201 : Return Cmd_GetStatus(gate, cmd, repl)
            Case &H100 : Return Cmd_CheckPdc(gate, cmd, repl)
            Case &H108 : Return Cmd_Dialup(gate, cmd, repl)
            Case &H10A : Return Cmd_Hangup(gate, cmd, repl)
            Case &H110 : Return Cmd_SetPppLogin(gate, cmd, repl)
            Case &H111 : Return Cmd_SetDns(gate, cmd, repl)
            Case &H1101 : Return Cmd_SockNew(gate, cmd, repl)
            Case &H1103 : Return Cmd_SockCon(gate, cmd, repl)
            Case &H1106 : Return Cmd_SockShutdown(gate, cmd, repl)
            Case &H1107 : Return Cmd_SockDel(gate, cmd, repl)
            Case &H1108 : Return Cmd_GetHost(gate, cmd, repl)
            Case &H110D, &H110F : Return Cmd_SockRecv(gate, cmd, repl)
            Case &H110E : Return Cmd_SockSend(gate, cmd, repl)
            Case Else
                WLog.Dbg($"Unhandled command {id:X4}!")
                Return Nothing
        End Select
    End Function

    Private Function Cmd_PowerOn(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        repl.Size = 2
        repl.SetWord(0, gate.PowerOn())
        Return repl
    End Function

    Private Function Cmd_PowerOff(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        ' Needs the 55 AA 55 AA confirmation, else no reply.
        If cmd.Size < 4 OrElse cmd.GetByte(0) <> &H55 OrElse cmd.GetByte(1) <> &HAA _
           OrElse cmd.GetByte(2) <> &H55 OrElse cmd.GetByte(3) <> &HAA Then
            WLog.Dbg("Power-off command with bad confirmation!")
            Return Nothing
        End If
        repl.Size = 1
        repl.SetByte(0, gate.PowerOff())
        Return repl
    End Function

    Private Function Cmd_GetStatus(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        repl.Cmd = &H2 ' reply uses cmd 0x02
        repl.Size = 1
        repl.SetByte(0, gate.Status)
        Return repl
    End Function

    Private Function Cmd_CheckPdc(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        repl.Size = 2
        repl.SetByte(0, gate.PdcStatus)
        repl.SetByte(1, gate.PdcReception)
        Return repl
    End Function

    Private Function Cmd_Dialup(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        ' header param[0..2] = 00 01 03, then [len][digits]
        Dim adr As Integer = 3
        Dim sz As Integer = cmd.GetByte(adr) : adr += 1
        Dim num As String = Nothing
        If sz > 0 Then num = AsciiSlice(cmd, adr, sz)
        repl.Cmd = &HB ' reply uses cmd 0x0B
        repl.Size = 1
        repl.SetByte(0, gate.Dial(num))
        Return repl
    End Function

    Private Function Cmd_Hangup(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        repl.Cmd = &HB
        repl.Size = 1
        repl.SetByte(0, gate.Hangup())
        Return repl
    End Function

    Private Function Cmd_SetPppLogin(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        ' header param[0]=07, param[5]=01, then [ulen][user][plen][pass]
        Dim adr As Integer = 6
        Dim ulen As Integer = cmd.GetByte(adr) : adr += 1
        Dim user As String = Nothing
        If ulen > 0 Then
            user = AsciiSlice(cmd, adr, ulen)
            adr += ulen
        End If
        Dim plen As Integer = cmd.GetByte(adr) : adr += 1
        Dim pass As String = Nothing
        If plen > 0 Then
            pass = AsciiSlice(cmd, adr, plen)
            adr += plen
        End If
        repl.Size = 1
        repl.SetByte(0, gate.SetPppLogin(user, pass))
        Return repl
    End Function

    Private Function Cmd_SetDns(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        Dim d1(3) As Byte, d2(3) As Byte
        For i = 0 To 3
            d1(i) = cmd.GetByte(&H9 + i)
            d2(i) = cmd.GetByte(&HD + i)
        Next
        repl.Size = 1
        repl.SetByte(0, gate.SetDns(d1, d2))
        Return repl
    End Function

    Private Function Cmd_SockNew(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        repl.Size = 1
        repl.SetByte(0, gate.SockNew())
        Return repl
    End Function

    Private Function Cmd_SockCon(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        Dim s As Byte = cmd.GetByte(0)
        Dim afam As UShort = CUShort((CInt(cmd.GetByte(1)) << 8) Or cmd.GetByte(2))
        Dim data As Byte() = Slice(cmd, 3, cmd.Size - 3)
        repl.Size = 2
        repl.SetByte(0, 0)
        repl.SetByte(1, gate.SockCon(s, afam, data, data.Length))
        Return repl
    End Function

    Private Function Cmd_SockShutdown(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        ' 0x1106 = shutdown socket. param[0]=sockid, param[1]=how.
        Dim s As Byte = cmd.GetByte(0)
        Dim how As Byte = cmd.GetByte(1)
        repl.Size = 1
        repl.SetByte(0, gate.SockShutdown(s, how))
        Return repl
    End Function

    Private Function Cmd_SockDel(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        repl.Size = 1
        repl.SetByte(0, gate.SockDel(cmd.GetByte(0)))
        Return repl
    End Function

    Private Function Cmd_SockSend(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        Dim s As Byte = cmd.GetByte(0)
        ' data = param[1..], length = size-1
        Dim ret As Byte = gate.SockSend(s, cmd.Param, 1, cmd.Size - 1)
        repl.Size = 2
        repl.SetByte(0, 0)
        repl.SetByte(1, ret)
        Return repl
    End Function

    Private Function Cmd_SockRecv(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        Dim s As Byte = cmd.GetByte(0)
        Dim len As Integer = cmd.GetByte(1)
        Dim tmp(255) As Byte
        Dim ret As Integer = gate.SockRecv(s, tmp, len)
        Dim n As Integer = If(ret > 0, ret, 0)
        repl.Size = 2 + n
        repl.SetByte(0, 0)
        repl.SetByte(1, CByte(If(ret < 0, &HFF, ret And &HFF)))
        For i = 0 To n - 1
            repl.SetByte(2 + i, tmp(i))
        Next
        Return repl
    End Function

    Private Function Cmd_GetHost(gate As WonGate, cmd As WonGateCmd, repl As WonGateCmd) As WonGateCmd
        ' name = null-terminated ASCII at start of params
        Dim sz As Integer = 0
        While sz < cmd.Param.Length AndAlso cmd.GetByte(sz) <> 0
            sz += 1
        End While
        Dim name As String = AsciiSlice(cmd, 0, sz)

        Dim canon As String = Nothing
        Dim ip As Byte() = Nothing
        Dim res As Byte = gate.GetHost(name, canon, ip)

        If res <> 0 Then
            Dim nameBytes = Encoding.ASCII.GetBytes(If(canon, ""))
            repl.Size = 1 + nameBytes.Length + 1 + 4
            repl.SetByte(0, res)
            Array.Copy(nameBytes, 0, repl.Param, 1, nameBytes.Length)
            repl.SetByte(1 + nameBytes.Length, 0) ' null terminator
            Dim ipOff As Integer = 1 + nameBytes.Length + 1
            repl.SetByte(ipOff + 0, ip(0))
            repl.SetByte(ipOff + 1, ip(1))
            repl.SetByte(ipOff + 2, ip(2))
            repl.SetByte(ipOff + 3, ip(3))
        Else
            repl.Size = 1
            repl.SetByte(0, res)
        End If
        Return repl
    End Function

    Private Function Slice(cmd As WonGateCmd, off As Integer, len As Integer) As Byte()
        If len <= 0 OrElse off >= cmd.Param.Length Then Return Array.Empty(Of Byte)()
        len = Math.Min(len, cmd.Param.Length - off)
        Dim b(len - 1) As Byte
        Array.Copy(cmd.Param, off, b, 0, len)
        Return b
    End Function

    Private Function AsciiSlice(cmd As WonGateCmd, off As Integer, len As Integer) As String
        Return Encoding.ASCII.GetString(Slice(cmd, off, len))
    End Function

End Module
