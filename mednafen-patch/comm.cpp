/* Mednafen - Multi-system Emulator
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

/*
 * WIN32 excomm support added for WonderGateway.
 */

#include "wswan.h"

#include <unistd.h>
#include <fcntl.h>
#include <sys/types.h>

#ifdef HAVE_FORK
#include <sys/wait.h>
#include <signal.h>
#endif

#if !defined(HAVE_FORK) && defined(_WIN32)
 #define WSWAN_COMM_WIN32 1
 #include <windows.h>
 #include <cstdio>
#endif

namespace MDFN_IEN_WSWAN
{

static uint8 Control;
static uint8 SendBuf, RecvBuf;
static bool SendLatched, RecvLatched;

#ifdef WSWAN_COMM_WIN32
static HANDLE child_proc      = NULL;
static HANDLE child_stdin_wr  = INVALID_HANDLE_VALUE;
static HANDLE child_stdout_rd = INVALID_HANDLE_VALUE;
#else
static int child_pid = -1;
static int stdin_pipes[2] = { -1, -1 };
static int stdout_pipes[2] = { -1, -1 };
#endif

void Comm_Init(const char *wfence_path)
{
#ifdef WSWAN_COMM_WIN32
 child_proc      = NULL;
 child_stdin_wr  = INVALID_HANDLE_VALUE;
 child_stdout_rd = INVALID_HANDLE_VALUE;

 if(wfence_path != NULL)
 {
  SECURITY_ATTRIBUTES sa;
  memset(&sa, 0, sizeof(sa));
  sa.nLength = sizeof(sa);
  sa.bInheritHandle = TRUE;
  sa.lpSecurityDescriptor = NULL;

  HANDLE in_rd = INVALID_HANDLE_VALUE,  in_wr  = INVALID_HANDLE_VALUE;
  HANDLE out_rd = INVALID_HANDLE_VALUE, out_wr = INVALID_HANDLE_VALUE;

  // Pipe for the child's stdin: child reads in_rd, we write in_wr.
  // Pipe for the child's stdout: child writes out_wr, we read out_rd.
  if(CreatePipe(&in_rd, &in_wr, &sa, 0) && CreatePipe(&out_rd, &out_wr, &sa, 0))
  {
   SetHandleInformation(in_wr,  HANDLE_FLAG_INHERIT, 0);
   SetHandleInformation(out_rd, HANDLE_FLAG_INHERIT, 0);

   STARTUPINFOA si;
   memset(&si, 0, sizeof(si));
   si.cb = sizeof(si);
   si.dwFlags    = STARTF_USESTDHANDLES;
   si.hStdInput  = in_rd;
   si.hStdOutput = out_wr;
   si.hStdError  = GetStdHandle(STD_ERROR_HANDLE);

   PROCESS_INFORMATION pi;
   memset(&pi, 0, sizeof(pi));

   
   char cmdline[2048];
   snprintf(cmdline, sizeof(cmdline), "\"%s\" ASDF", wfence_path);
   BOOL ok = CreateProcessA(NULL, cmdline, NULL, NULL,
                            TRUE,           
                            CREATE_NO_WINDOW,
                            NULL, NULL, &si, &pi);
   CloseHandle(in_rd);
   CloseHandle(out_wr);

   if(ok)
   {
    CloseHandle(pi.hThread);
    child_proc      = pi.hProcess;
    child_stdin_wr  = in_wr;
    child_stdout_rd = out_rd;
   }
   else
   {
    CloseHandle(in_wr);
    CloseHandle(out_rd);
   }
  }
  else
  {
   if(in_rd  != INVALID_HANDLE_VALUE) CloseHandle(in_rd);
   if(in_wr  != INVALID_HANDLE_VALUE) CloseHandle(in_wr);
   if(out_rd != INVALID_HANDLE_VALUE) CloseHandle(out_rd);
   if(out_wr != INVALID_HANDLE_VALUE) CloseHandle(out_wr);
  }
 }
#else
 child_pid = -1;

 stdin_pipes[0] = -1;
 stdin_pipes[1] = -1;

 stdout_pipes[0] = -1;
 stdout_pipes[1] = -1;

#ifdef HAVE_FORK
 if(wfence_path != NULL)
 {
  pipe(stdin_pipes);
  pipe(stdout_pipes);

  child_pid = fork();

  if(child_pid == -1)
   abort();
  else if(child_pid == 0)
  {
   dup2(stdin_pipes[0], 0);
   dup2(stdout_pipes[1], 1);
   execlp(wfence_path, wfence_path, "ASDF", (char*)NULL);
   abort();
  }

  fcntl(stdout_pipes[0], F_SETFL, fcntl(stdout_pipes[0], F_GETFL) | O_NONBLOCK);
 }
#endif
#endif
}

void Comm_Kill(void)
{
#ifdef WSWAN_COMM_WIN32
 if(child_proc != NULL)
 {
  TerminateProcess(child_proc, 0);
  WaitForSingleObject(child_proc, 1000);
  CloseHandle(child_proc);
  child_proc = NULL;
 }

 if(child_stdin_wr != INVALID_HANDLE_VALUE)
 {
  CloseHandle(child_stdin_wr);
  child_stdin_wr = INVALID_HANDLE_VALUE;
 }
 if(child_stdout_rd != INVALID_HANDLE_VALUE)
 {
  CloseHandle(child_stdout_rd);
  child_stdout_rd = INVALID_HANDLE_VALUE;
 }
#else
#ifdef HAVE_FORK
 if(child_pid != -1)
 {
  int status;

  kill(child_pid, SIGTERM);
  waitpid(child_pid, &status, 0);

  child_pid = -1;
 }
#endif

 for(unsigned i = 0; i < 2; i++)
 {
  if(stdin_pipes[i] != -1)
  {
   close(stdin_pipes[i]);
   stdin_pipes[i] = -1;
  }

  if(stdout_pipes[i] != -1)
  {
   close(stdout_pipes[i]);
   stdout_pipes[i] = -1;
  }
 }
#endif
}

void Comm_Reset(void)
{
 SendBuf = 0x00;
 RecvBuf = 0x00;

 SendLatched = false;
 RecvLatched = false;

 Control = 0x00;

 WSwan_InterruptAssert(WSINT_SERIAL_RECV, RecvLatched);
}

void Comm_Process(void)
{
 if(SendLatched && (Control & 0x80))
 {
#ifdef WSWAN_COMM_WIN32
  if(child_stdin_wr != INVALID_HANDLE_VALUE)
  {
   DWORD nw = 0;
   if(WriteFile(child_stdin_wr, &SendBuf, 1, &nw, NULL) && nw == 1)
   {
    SendLatched = false;
    WSwan_Interrupt(WSINT_SERIAL_SEND);
   }
  }
  else
  {
   SendLatched = false;
   WSwan_Interrupt(WSINT_SERIAL_SEND);
  }
#else
  if(stdin_pipes[1] != -1)
  {
   if(write(stdin_pipes[1], &SendBuf, 1) == 1)
   {
    SendLatched = false;
    WSwan_Interrupt(WSINT_SERIAL_SEND);
   }
  }
  else
  {
   SendLatched = false;
   WSwan_Interrupt(WSINT_SERIAL_SEND);
  }
#endif
 }
 else if(!RecvLatched && (Control & 0x20))
 {
#ifdef WSWAN_COMM_WIN32
  if(child_stdout_rd != INVALID_HANDLE_VALUE)
  {
   DWORD avail = 0;
   // Non-blocking: only read when a byte is actually waiting.
   if(PeekNamedPipe(child_stdout_rd, NULL, 0, NULL, &avail, NULL) && avail > 0)
   {
    DWORD nr = 0;
    if(ReadFile(child_stdout_rd, &RecvBuf, 1, &nr, NULL) && nr == 1)
    {
     RecvLatched = true;
     WSwan_InterruptAssert(WSINT_SERIAL_RECV, RecvLatched);
    }
   }
  }
#else
  if(stdout_pipes[0] != -1)
  {
   if(read(stdout_pipes[0], &RecvBuf, 1) == 1)
   {
    RecvLatched = true;
    WSwan_InterruptAssert(WSINT_SERIAL_RECV, RecvLatched);
   }
  }
#endif
 }
}

uint8 Comm_Read(uint8 A)
{
 if(A == 0xB1)
 {
  if(!WS_InDebug)
  {
   RecvLatched = false;
   WSwan_InterruptAssert(WSINT_SERIAL_RECV, RecvLatched);
  }

  return(RecvBuf);
 }
 else if(A == 0xB3)
 {
  uint8 ret = Control & 0xF0;

  if((Control & 0x80) && !SendLatched)
   ret |= 0x4;

  if((Control & 0x20) && RecvLatched)
   ret |= 0x1;

  return(ret);
 }

 return(0x00);
}

void Comm_Write(uint8 A, uint8 V)
{
 if(A == 0xB1)
 {
  if(Control & 0x80)
  {
   SendBuf = V;
   SendLatched = true;
  }
 }
 else if(A == 0xB3)
 {
  Control = V & 0xF0;
 }
}

void Comm_StateAction(StateMem *sm, const unsigned load, const bool data_only)
{
 SFORMAT StateRegs[] =
 {
  SFVAR(SendBuf),
  SFVAR(RecvBuf),

  SFVAR(SendLatched),
  SFVAR(RecvLatched),

  SFVAR(Control),

  SFEND
 };

 if(load && load < 0x0936)
 {
  SendBuf = 0x00;
  RecvBuf = 0x00;

  SendLatched = false;
  RecvLatched = false;

  Control = 0x00;
 }
 else
 {
  MDFNSS_StateAction(sm, load, data_only, StateRegs, "COMM");

  if(load)
  {
   WSwan_InterruptAssert(WSINT_SERIAL_RECV, RecvLatched);
  }
 }
}

}
