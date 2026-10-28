#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
int main(int argc,char **argv) {
    if(argc!=2)return 2;
    HMODULE dll=LoadLibraryA(argv[1]);
    if(!dll){printf("LoadLibrary error %lu\n",GetLastError());return 3;}
    typedef DWORD (WINAPI *SizeFn)(LPCWSTR,LPDWORD);
    SizeFn fn=(SizeFn)(void *)GetProcAddress(dll,"GetFileVersionInfoSizeW");
    if(!fn)return 4;
    wchar_t path[MAX_PATH];GetSystemDirectoryW(path,MAX_PATH-20);lstrcatW(path,L"\\version.dll");
    DWORD ignored=0,size=fn(path,&ignored);
    printf("version.dll forwarding: %lu bytes\n",size);
    return size?0:5;
}
