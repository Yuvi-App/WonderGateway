Imports System.Linq

' A command/reply packet. Wire layout: [type][zero][sizeField][cmd][param...]
' where sizeField counts the cmd byte, so param count = sizeField - 1.
Public Class WonGateCmd
    Public Type As Byte
    Public Zero As Byte
    Public Cmd As Byte
    Public Param As Byte() = Array.Empty(Of Byte)()

    ' Number of parameter bytes (excludes the cmd byte).
    Public Property Size As Integer
        Get
            Return Param.Length
        End Get
        Set(value As Integer)
            If value <= 0 Then
                Param = Array.Empty(Of Byte)()
            Else
                Param = New Byte(value - 1) {}
            End If
        End Set
    End Property

    Public Sub SetByte(adr As Integer, val As Byte)
        Param(adr) = val
    End Sub

    Public Function GetByte(adr As Integer) As Byte
        If adr < 0 OrElse adr >= Param.Length Then Return 0
        Return Param(adr)
    End Function

    ' Writes a 16-bit value little-endian.
    Public Sub SetWord(adr As Integer, val As UShort)
        Param(adr) = CByte(val And &HFF)
        Param(adr + 1) = CByte((CInt(val) >> 8) And &HFF)
    End Sub

    Public Function HexParams() As String
        If Param.Length = 0 Then Return ""
        Return "(" & String.Join(",", Param.Select(Function(b) b.ToString("X2"))) & ")"
    End Function

    ' Reads one command from the transport; Nothing at end-of-stream.
    Public Shared Function Recv(t As IByteTransport) As WonGateCmd
        Dim cmd As New WonGateCmd()
        Dim b As Integer

        b = t.ReadByte() : If b < 0 Then Return Nothing
        cmd.Type = CByte(b)
        b = t.ReadByte() : If b < 0 Then Return Nothing
        cmd.Zero = CByte(b)
        b = t.ReadByte() : If b < 0 Then Return Nothing
        Dim sizeField As Integer = b

        Dim paramCount As Integer = 0
        If sizeField > 0 Then
            b = t.ReadByte() : If b < 0 Then Return Nothing
            cmd.Cmd = CByte(b)
            paramCount = sizeField - 1
        Else
            cmd.Cmd = 0
        End If

        If paramCount > 0 Then
            Dim buf As Byte() = New Byte(paramCount - 1) {}
            Dim got As Integer = 0
            While got < paramCount
                b = t.ReadByte()
                If b < 0 Then Return Nothing  ' truncated
                buf(got) = CByte(b)
                got += 1
            End While
            cmd.Param = buf
        Else
            cmd.Param = Array.Empty(Of Byte)()
        End If

        Return cmd
    End Function

    ' Writes this reply; wire size field is (param count + 1) for the cmd byte.
    Public Sub Send(t As IByteTransport)
        WLog.Dbg($"=> Send Type:Cmd={Type:X2}:{Cmd:X2} Size={Size:X2} {HexParams()}")
        t.WriteByte(Type)
        t.WriteByte(Zero)
        t.WriteByte(CByte((Size + 1) And &HFF))
        t.WriteByte(Cmd)
        If Param.Length > 0 Then t.WriteBytes(Param)
        t.Flush()
    End Sub
End Class
