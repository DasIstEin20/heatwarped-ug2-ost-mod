#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <wchar.h>
#include <string.h>
#include <stdarg.h>
#include "../vendor/minhook/include/MinHook.h"
#include "music_guids.h"

/* A local version.dll proxy. Only the known soundtrack event GUIDs are replaced.
   All other FMOD events and every Windows version API call retain their original behavior. */
#define MAX_TRACKS 1024
#define MAX_INSTANCES 128
typedef int (*FnStart)(void *);
typedef int (*FnStop)(void *, int);
typedef int (*FnVolume)(void *, float);
typedef int (*FnUpdate)(void *);
typedef int (*FnDescription)(void *, void **);
typedef int (*FnGuid)(void *, void *);
typedef int (*FnCreateStream)(void *, const char *, unsigned, void *, void **);
typedef int (*FnPlay)(void *, void *, void *, int, void **);
typedef int (*FnChannelBool)(void *, int *);
typedef int (*FnChannelVolume)(void *, float);
typedef int (*FnChannelPause)(void *, int);
typedef int (*FnOne)(void *);
typedef int (*FnGetCore)(void *, void **);
typedef int (*FnGetGroup)(void *, void **);
typedef int (*FnCreateGroup)(void *, const char *, void **);
typedef int (*FnAddGroup)(void *, void *, int, void **);
typedef int (*FnPosition)(void *, unsigned *, unsigned);
typedef int (*FnCreateInstance)(void *,void **);
typedef int (*FnGetBus)(void *,const char *,void **);
typedef int (*FnBusCount)(void *,int *);
typedef int (*FnBusList)(void *,void **,int,int *);
typedef int (*FnBusPath)(void *,char *,int,int *);
typedef int (*FnEventById)(void *,const void *,void **);
typedef int (*FnGetVolume)(void *,float *,float *);
typedef int (*FnPlaybackState)(void *,int *);

typedef struct { wchar_t file[1024], title[512]; int menu, drive; } Track;
typedef struct { void *instance; int context, active; float volume; } Instance;
static Track tracks[MAX_TRACKS];
static int track_count, enabled=1, shuffle=1, context, current_track=-1, last_track[3]={-1,-1,-1};
static int bag[MAX_TRACKS], bag_count, bag_position, bag_context;
static float gain=0.8f, event_volume=1.f;
static Instance instances[MAX_INSTANCES];
static void *core, *sound, *channel, *music_group, *active_instance;
static ULONGLONG stop_after, last_config_check, last_status, retry_after;
static FILETIME config_time;
static int config_seen;
static wchar_t game_dir[1024], config_path[1200], log_path[1200], status_path[1200];
static LONG skip_requested;
static FnStart original_start;
static FnStop original_stop;
static FnVolume original_volume;
static FnUpdate original_update;
static FnDescription get_description;
static FnGuid get_guid;
static FnGetCore get_core;
static FnCreateStream create_stream;
static FnPlay play_sound;
static FnChannelBool is_playing;
static FnChannelVolume set_channel_volume;
static FnChannelPause set_channel_paused;
static FnOne stop_channel, release_sound;
static FnGetGroup get_master_group;
static FnCreateGroup create_group;
static FnAddGroup add_group;
static FnPosition get_position;
static FnCreateInstance original_create_instance;
static FnGetBus get_bus;
static FnGetGroup bus_get_group;
static FnBusCount get_bus_count;
static FnBusList get_bus_list;
static FnBusPath get_bus_path;
static FnBusCount bank_bus_count;
static FnBusList bank_bus_list;
static FnEventById get_event_by_id;
static FnBusList get_instance_list;
static FnGetVolume get_event_volume;
static FnPlaybackState get_playback_state;
static ULONGLONG last_recover;
static int buses_logged, group_routed;
static FnOne lock_bus;
static int music_bus_locked;

static void log_line(const char *fmt, ...) {
    FILE *f=_wfopen(log_path,L"ab"); if(!f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f,"%02d:%02d:%02d ",t.wHour,t.wMinute,t.wSecond);
    va_list args; va_start(args,fmt); vfprintf(f,fmt,args); va_end(args);
    fputs("\r\n",f); fclose(f);
}
static int save_config(void);
static void next_track(void);
static int has_playlist(int wanted);
#include "unity_overlay.h"
static void utf8(const wchar_t *src,char *dest,int size) {
    if(!WideCharToMultiByte(CP_UTF8,0,src,-1,dest,size,NULL,NULL)) dest[0]=0;
}
static void write_status(const char *state) {
    FILE *f=_wfopen(status_path,L"wb"); if(!f) return;
    char title[2048]="";
    if(current_track>=0 && current_track<track_count) utf8(tracks[current_track].title,title,sizeof(title));
    unsigned position=0;
    if(channel && get_position) get_position(channel,&position,1); /* FMOD_TIMEUNIT_MS */
    fprintf(f,"%s\n%s\n%s\n%u\n",state,context==1?"Menu":context==2?"Driving / racing":"Waiting",title,position);
    fclose(f);
}
static int eligible(int index, int wanted) {
    return index>=0 && index<track_count && ((wanted==1 && tracks[index].menu) || (wanted==2 && tracks[index].drive));
}
static int has_playlist(int wanted) {
    if(!enabled) return 0;
    for(int i=0;i<track_count;i++) if(eligible(i,wanted)) return 1;
    return 0;
}
static void end_track(void) {
    if(channel) stop_channel(channel);
    channel=NULL;
    if(sound) release_sound(sound);
    sound=NULL; current_track=-1;
}
static int choose_track(void) {
    if(bag_context!=context || bag_position>=bag_count) {
        bag_count=0; bag_position=0; bag_context=context;
        for(int i=0;i<track_count;i++) if(eligible(i,context)) bag[bag_count++]=i;
        if(shuffle) {
            for(int i=bag_count-1;i>0;i--) { int j=rand()%(i+1),v=bag[i]; bag[i]=bag[j];bag[j]=v; }
            if(bag_count>1 && bag[0]==last_track[context]) { int v=bag[0];bag[0]=bag[1];bag[1]=v; }
        }
    }
    return bag_position<bag_count?bag[bag_position++]:-1;
}
static void next_track(void) {
    end_track();
    if(!core || !has_playlist(context)) return;
    for(int attempt=0;attempt<track_count;attempt++) {
        int index=choose_track(); if(index<0) break;
        wchar_t full[2200];
        if(tracks[index].file[1]==L':' || tracks[index].file[0]==L'\\')
            swprintf(full,2200,L"%ls",tracks[index].file);
        else swprintf(full,2200,L"%ls\\CustomMusic\\%ls",game_dir,tracks[index].file);
        char path[8800];utf8(full,path,sizeof(path));
        int r=create_stream(core,path,0x00000089,NULL,&sound); /* 2D, LOOP_OFF, CREATESTREAM */
        if(r) { log_line("CreateStream failed: %d, %s",r,path); sound=NULL; continue; }
        r=play_sound(core,sound,music_group,1,&channel);
        if(r) { log_line("PlaySound failed: %d",r); channel=NULL;release_sound(sound);sound=NULL;continue; }
        current_track=index;last_track[context]=index;
        set_channel_volume(channel,event_volume*gain);
        set_channel_paused(channel,0);
        char title[2048];utf8(tracks[index].title,title,sizeof(title));
        log_line("Playing [%s] %s (event volume %.3f, gain %.3f)",context==1?"menu":"drive",title,event_volume,gain);
        retry_after=0;overlay_notify();write_status("Playing");return;
    }
    retry_after=GetTickCount64()+5000;
    log_line("No playable files for context %d",context); write_status("No playable tracks");
}
static Instance *find_instance(void *instance,int create) {
    for(int i=0;i<MAX_INSTANCES;i++) if(instances[i].instance==instance) return &instances[i];
    if(!create) return NULL;
    void *description=NULL;unsigned char guid[16];
    if(get_description(instance,&description) || !description || get_guid(description,guid)) return NULL;
    int wanted=0;
    for(unsigned i=0;i<sizeof(music_guids)/sizeof(music_guids[0]);i++) {
        if(memcmp(music_guids[i].guid,guid,16)==0) { wanted=music_guids[i].context;break; }
    }
    if(!wanted) return NULL;
    for(int i=0;i<MAX_INSTANCES;i++) if(!instances[i].instance || (!instances[i].active && instances[i].instance!=active_instance)) {
        instances[i]=(Instance){.instance=instance,.context=wanted,.active=0,.volume=1.f};
        return &instances[i];
    }
    return NULL;
}
static void synchronize_mute(void) {
    for(int i=0;i<MAX_INSTANCES;i++) if(instances[i].active) {
        original_volume(instances[i].instance,has_playlist(instances[i].context)?0.f:instances[i].volume);
    }
}
static void load_config(void) {
    WIN32_FILE_ATTRIBUTE_DATA info;
    if(!GetFileAttributesExW(config_path,GetFileExInfoStandard,&info)) return;
    if(config_seen && CompareFileTime(&info.ftLastWriteTime,&config_time)==0) return;
    HANDLE f=CreateFileW(config_path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,0,NULL);
    if(f==INVALID_HANDLE_VALUE) return;
    DWORD size=GetFileSize(f,NULL),read=0;
    if(size<4 || size>2*1024*1024) { CloseHandle(f);return; }
    unsigned char *bytes=calloc(size+4,1);if(!bytes){CloseHandle(f);return;}
    BOOL ok=ReadFile(f,bytes,size,&read,NULL);CloseHandle(f);
    if(!ok || read!=size || bytes[0]!=0xff || bytes[1]!=0xfe) { free(bytes);return; }
    Track *loaded=calloc(MAX_TRACKS,sizeof(Track)); if(!loaded) {free(bytes);return;}
    int count=0,index=-1,new_enabled=1,new_shuffle=1;
    float new_gain=.8f; wchar_t *save=NULL;
    for(wchar_t *line=wcstok((wchar_t *)(bytes+2),L"\r\n",&save);line;line=wcstok(NULL,L"\r\n",&save)) {
        if(line[0]==L'[') { if(swscanf(line,L"[Track%d]",&index)!=1) index=-1;continue; }
        wchar_t *eq=wcschr(line,L'=');if(!eq) continue;*eq++=0;
        if(index<0) {
            if(!wcscmp(line,L"Enabled"))new_enabled=_wtoi(eq)!=0;
            else if(!wcscmp(line,L"Shuffle"))new_shuffle=_wtoi(eq)!=0;
            else if(!wcscmp(line,L"Gain"))new_gain=(float)wcstod(eq,NULL);
            else if(!wcscmp(line,L"Count"))count=_wtoi(eq);
        } else if(index<MAX_TRACKS) {
            if(!wcscmp(line,L"File"))wcsncpy(loaded[index].file,eq,1023);
            else if(!wcscmp(line,L"Title"))wcsncpy(loaded[index].title,eq,511);
            else if(!wcscmp(line,L"Menu"))loaded[index].menu=_wtoi(eq)!=0;
            else if(!wcscmp(line,L"Drive"))loaded[index].drive=_wtoi(eq)!=0;
        }
    }
    if(count<0 || count>MAX_TRACKS || new_gain<0 || new_gain>1) {free(loaded);free(bytes);return;}
    wchar_t playing_file[1024]=L"";
    if(current_track>=0 && current_track<track_count)wcscpy(playing_file,tracks[current_track].file);
    memcpy(tracks,loaded,sizeof(tracks));track_count=count;
    enabled=new_enabled;shuffle=new_shuffle;gain=new_gain;
    bag_count=bag_position=bag_context=0;
    current_track=-1;
    for(int i=0;i<track_count;i++) if(playing_file[0] && !wcscmp(playing_file,tracks[i].file))current_track=i;
    config_time=info.ftLastWriteTime;config_seen=1;
    free(loaded);free(bytes);
    synchronize_mute();
    retry_after=0;
    if(channel && !eligible(current_track,context))end_track();
    if(!enabled && channel)end_track();
    if(channel)set_channel_volume(channel,event_volume*gain);
    log_line("Configuration loaded: %d tracks, enabled=%d shuffle=%d gain=%.3f",track_count,enabled,shuffle,gain);
}
static int save_config(void) {
    size_t capacity=256+(size_t)track_count*1700;
    wchar_t *text=calloc(capacity,sizeof(wchar_t));if(!text)return 0;
    int used=swprintf(text,capacity,L"[General]\r\nEnabled=%d\r\nShuffle=%d\r\nGain=%.3f\r\nCount=%d\r\n",enabled,shuffle,gain,track_count);
    for(int i=0;i<track_count && used>0;i++) {
        int written=swprintf(text+used,capacity-used,L"\r\n[Track%d]\r\nFile=%ls\r\nTitle=%ls\r\nMenu=%d\r\nDrive=%d\r\n",i,tracks[i].file,tracks[i].title,tracks[i].menu,tracks[i].drive);
        if(written<0){free(text);return 0;}used+=written;
    }
    wchar_t temporary[1300];swprintf(temporary,1300,L"%ls.overlay-%lu.tmp",config_path,GetCurrentProcessId());
    HANDLE file=CreateFileW(temporary,GENERIC_WRITE,0,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);
    int ok=0;
    if(file!=INVALID_HANDLE_VALUE) {
        WORD bom=0xfeff;DWORD wrote=0,bytes=(DWORD)(used*sizeof(wchar_t));
        ok=WriteFile(file,&bom,2,&wrote,NULL) && wrote==2 && WriteFile(file,text,bytes,&wrote,NULL) && wrote==bytes;
        CloseHandle(file);
        if(ok)ok=MoveFileExW(temporary,config_path,MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=0;
        if(!ok)DeleteFileW(temporary);
    }
    free(text);
    if(ok)load_config();
    else log_line("In-game settings save failed: %lu",GetLastError());
    return ok;
}
static int hooked_create_instance(void *description,void **output) {
    int result=original_create_instance(description,output);
    if(!result && output && *output) {
        /* FMOD may recycle the address of an instance after release. */
        for(int i=0;i<MAX_INSTANCES;i++) if(instances[i].instance==*output) {
            instances[i].instance=NULL;instances[i].active=0;break;
        }
        find_instance(*output,1);
    }
    return result;
}
static int hooked_start(void *instance) {
    Instance *item=find_instance(instance,1);
    if(item) {
        item->active=1;
        int new_context=item->context;
        if(context!=new_context) {
            end_track();context=new_context;bag_context=0;
            log_line("Context changed: %s",context==1?"menu":"drive");
        }
        active_instance=instance;event_volume=item->volume;stop_after=0;
        if(has_playlist(context))original_volume(instance,0.f);
    }
    return original_start(instance);
}
static int hooked_stop(void *instance,int mode) {
    Instance *item=find_instance(instance,0);
    if(item) {item->active=0;if(active_instance==instance)stop_after=GetTickCount64()+700;}
    return original_stop(instance,mode);
}
static int hooked_volume(void *instance,float volume) {
    Instance *item=find_instance(instance,1);
    if(!item) return original_volume(instance,volume);
    item->volume=volume;
    if(instance==active_instance) {
        event_volume=volume;
        if(channel)set_channel_volume(channel,volume*gain);
    }
    return original_volume(instance,has_playlist(item->context)?0.f:volume);
}
static int hooked_update(void *studio) {
    unity_overlay_tick();
    if(!core && !get_core(studio,&core) && core) {
        void *master=NULL;
        if(!get_master_group(core,&master) && master && !create_group(core,"HeatwarpedCustomMusic",&music_group))
            add_group(master,music_group,1,NULL);
        log_line("FMOD core captured, custom group=%p",music_group);
    }
    ULONGLONG now=GetTickCount64();
    if(core && !buses_logged && get_bus_count && get_bus_list && get_bus_path) {
        int count=0;
        if(!get_bus_count(studio,&count) && count>0 && count<128) {
            void *buses[128];int retrieved=0;
            if(!get_bus_list(studio,buses,128,&retrieved)) {
                for(int b=0;b<retrieved;b++) {
                    void *bank_buses[128];int nb=0;
                    if(!bank_bus_list(buses[b],bank_buses,128,&nb) && nb>0) {
                        for(int i=0;i<nb;i++){char path[512];int length=0;if(!get_bus_path(bank_buses[i],path,512,&length))log_line("Studio bus: %s",path);}
                        buses_logged=1;
                    }
                }
            }
        }
    }
    if(core && music_group && !group_routed && get_bus && bus_get_group) {
        void *root_bus=NULL,*root_group=NULL;
        if(!get_bus(studio,"bus:/Music",&root_bus)) {
            if(!music_bus_locked && lock_bus && !lock_bus(root_bus))music_bus_locked=1;
            if(!bus_get_group(root_bus,&root_group) && root_group && !add_group(root_group,music_group,1,NULL)) {
                group_routed=1;log_line("Custom group routed through Studio music bus.");
            }
        }
    }
    if(now-last_config_check>750) {last_config_check=now;load_config();}
    if(!active_instance && now-last_recover>1000 && get_event_by_id && get_instance_list && get_playback_state && get_event_volume) {
        last_recover=now;
        for(unsigned i=0;i<sizeof(music_guids)/sizeof(music_guids[0]) && !active_instance;i++) {
            void *description=NULL,*found[32];int count=0;
            if(get_event_by_id(studio,music_guids[i].guid,&description))continue;
            if(get_instance_list(description,found,32,&count))continue;
            for(int j=0;j<count;j++) {
                int state=2;
                if(get_playback_state(found[j],&state) || (state!=0 && state!=1 && state!=3))continue;
                Instance *item=find_instance(found[j],1);if(!item)continue;
                float volume=1.f,final_volume=1.f;
                get_event_volume(found[j],&volume,&final_volume);
                item->volume=volume;item->active=1;
                context=item->context;active_instance=found[j];event_volume=volume;stop_after=0;
                if(has_playlist(context))original_volume(active_instance,0.f);
                log_line("Recovered existing music instance, context=%s volume=%.3f",context==1?"menu":"drive",volume);
                break;
            }
        }
    }
    if(stop_after && now>=stop_after) {
        stop_after=0;end_track();context=0;active_instance=NULL;write_status("Waiting");
    }
    int race_next=context==2 && !stop_after && InterlockedExchange(&race_requested,0);
    if(InterlockedExchange(&skip_requested,0) || race_next) {
        if(context)next_track();
    }
    if(enabled && has_playlist(context) && !stop_after) {
        int playing=0;
        if(channel && is_playing(channel,&playing))playing=0;
        if(!playing && now>=retry_after)next_track();
    }
    if(channel && now-last_status>1000) {last_status=now;write_status("Playing");}
    return original_update(studio);
}
static void *require_proc(HMODULE module,const char *name) {
    FARPROC result=GetProcAddress(module,name);
    if(!result)log_line("Missing FMOD API: %s",name);
    return (void *)result;
}
static int install_hooks(HMODULE module) {
    get_description=(FnDescription)require_proc(module,"FMOD_Studio_EventInstance_GetDescription");
    get_guid=(FnGuid)require_proc(module,"FMOD_Studio_EventDescription_GetID");
    get_core=(FnGetCore)require_proc(module,"FMOD_Studio_System_GetCoreSystem");
    create_stream=(FnCreateStream)require_proc(module,"FMOD_System_CreateStream");
    play_sound=(FnPlay)require_proc(module,"FMOD_System_PlaySound");
    is_playing=(FnChannelBool)require_proc(module,"FMOD_Channel_IsPlaying");
    set_channel_volume=(FnChannelVolume)require_proc(module,"FMOD_Channel_SetVolume");
    set_channel_paused=(FnChannelPause)require_proc(module,"FMOD_Channel_SetPaused");
    stop_channel=(FnOne)require_proc(module,"FMOD_Channel_Stop");
    release_sound=(FnOne)require_proc(module,"FMOD_Sound_Release");
    get_master_group=(FnGetGroup)require_proc(module,"FMOD_System_GetMasterChannelGroup");
    create_group=(FnCreateGroup)require_proc(module,"FMOD_System_CreateChannelGroup");
    add_group=(FnAddGroup)require_proc(module,"FMOD_ChannelGroup_AddGroup");
    get_position=(FnPosition)require_proc(module,"FMOD_Channel_GetPosition");
    get_bus=(FnGetBus)require_proc(module,"FMOD_Studio_System_GetBus");
    bus_get_group=(FnGetGroup)require_proc(module,"FMOD_Studio_Bus_GetChannelGroup");
    lock_bus=(FnOne)require_proc(module,"FMOD_Studio_Bus_LockChannelGroup");
    get_bus_count=(FnBusCount)require_proc(module,"FMOD_Studio_System_GetBankCount");
    get_bus_list=(FnBusList)require_proc(module,"FMOD_Studio_System_GetBankList");
    bank_bus_count=(FnBusCount)require_proc(module,"FMOD_Studio_Bank_GetBusCount");
    bank_bus_list=(FnBusList)require_proc(module,"FMOD_Studio_Bank_GetBusList");
    get_bus_path=(FnBusPath)require_proc(module,"FMOD_Studio_Bus_GetPath");
    get_event_by_id=(FnEventById)require_proc(module,"FMOD_Studio_System_GetEventByID");
    get_instance_list=(FnBusList)require_proc(module,"FMOD_Studio_EventDescription_GetInstanceList");
    get_event_volume=(FnGetVolume)require_proc(module,"FMOD_Studio_EventInstance_GetVolume");
    get_playback_state=(FnPlaybackState)require_proc(module,"FMOD_Studio_EventInstance_GetPlaybackState");
    if(!get_description||!get_guid||!get_core||!create_stream||!play_sound||!is_playing||!set_channel_volume||!set_channel_paused||!stop_channel||!release_sound||!get_master_group||!create_group||!add_group||!get_position)return 0;
    if(MH_Initialize()!=MH_OK)return 0;
    struct {const char *name;void *hook;void **original;} hooks[]={
        {"FMOD_Studio_EventDescription_CreateInstance",hooked_create_instance,(void **)&original_create_instance},
        {"FMOD_Studio_EventInstance_Start",hooked_start,(void **)&original_start},
        {"FMOD_Studio_EventInstance_Stop",hooked_stop,(void **)&original_stop},
        {"FMOD_Studio_EventInstance_SetVolume",hooked_volume,(void **)&original_volume},
        {"FMOD_Studio_System_Update",hooked_update,(void **)&original_update}
    };
    for(unsigned i=0;i<sizeof(hooks)/sizeof(hooks[0]);i++) {
        void *p=(void *)require_proc(module,hooks[i].name);
        MH_STATUS result=MH_CreateHook(p,hooks[i].hook,hooks[i].original);
        if(result!=MH_OK) {log_line("Hook creation failed %s: %d",hooks[i].name,result);MH_Uninitialize();return 0;}
    }
    load_config();
    if(MH_EnableHook(MH_ALL_HOOKS)!=MH_OK) {MH_Uninitialize();return 0;}
    log_line("Music hooks installed. F8=settings, F9=next track.");
    write_status("Waiting");return 1;
}
static DWORD WINAPI bootstrap(void *unused) {
    (void)unused;srand((unsigned)GetTickCount());
    log_line("CustomMusic 1.2 starting, process=%lu",GetCurrentProcessId());
    HMODULE fmod=NULL;
    for(int i=0;i<2400 && !fmod;i++) {fmod=GetModuleHandleW(L"fmodstudio.dll");if(!fmod)Sleep(25);}
    if(!fmod || !install_hooks(fmod)) {log_line("Music extension inactive: FMOD initialization failed.");return 0;}
    int f8_was=0,f9_was=0;
    for(;;) {
        DWORD foreground_pid=0;GetWindowThreadProcessId(GetForegroundWindow(),&foreground_pid);
        int f8=(GetAsyncKeyState(VK_F8)&0x8000)!=0,f9=(GetAsyncKeyState(VK_F9)&0x8000)!=0;
        if(foreground_pid==GetCurrentProcessId()) {
            if(f8 && !f8_was) {
                InterlockedExchange(&overlay_toggle_requested,1);
            }
            if(f9 && !f9_was)InterlockedExchange(&skip_requested,1);
        }
        f8_was=f8;f9_was=f9;Sleep(40);
    }
    return 0;
}

/* Forward the Windows version library, preserving the ABI with a direct jump. */
void *version_functions[17];
#define FORWARD(name,offset) __attribute__((naked)) void proxy_##name(void) { __asm__("jmp *version_functions+" #offset "(%rip)"); }
FORWARD(GetFileVersionInfoA,0)
FORWARD(GetFileVersionInfoByHandle,8)
FORWARD(GetFileVersionInfoExA,16)
FORWARD(GetFileVersionInfoExW,24)
FORWARD(GetFileVersionInfoSizeA,32)
FORWARD(GetFileVersionInfoSizeExA,40)
FORWARD(GetFileVersionInfoSizeExW,48)
FORWARD(GetFileVersionInfoSizeW,56)
FORWARD(GetFileVersionInfoW,64)
FORWARD(VerFindFileA,72)
FORWARD(VerFindFileW,80)
FORWARD(VerInstallFileA,88)
FORWARD(VerInstallFileW,96)
FORWARD(VerLanguageNameA,104)
FORWARD(VerLanguageNameW,112)
FORWARD(VerQueryValueA,120)
FORWARD(VerQueryValueW,128)
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved) {
    (void)reserved;
    if(reason==DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        wchar_t path[1200];GetSystemDirectoryW(path,1100);wcscat(path,L"\\version.dll");
        HMODULE real=LoadLibraryW(path);if(!real)return FALSE;
        const char *names[]={"GetFileVersionInfoA","GetFileVersionInfoByHandle","GetFileVersionInfoExA","GetFileVersionInfoExW","GetFileVersionInfoSizeA","GetFileVersionInfoSizeExA","GetFileVersionInfoSizeExW","GetFileVersionInfoSizeW","GetFileVersionInfoW","VerFindFileA","VerFindFileW","VerInstallFileA","VerInstallFileW","VerLanguageNameA","VerLanguageNameW","VerQueryValueA","VerQueryValueW"};
        for(int i=0;i<17;i++)version_functions[i]=(void *)GetProcAddress(real,names[i]);
        GetModuleFileNameW(NULL,game_dir,1024);wchar_t *slash=wcsrchr(game_dir,L'\\');if(slash)*slash=0;
        swprintf(config_path,1200,L"%ls\\CustomMusic\\playlists.ini",game_dir);
        swprintf(log_path,1200,L"%ls\\CustomMusic\\music.log",game_dir);
        swprintf(status_path,1200,L"%ls\\CustomMusic\\now-playing.txt",game_dir);
        HANDLE thread=CreateThread(NULL,0,bootstrap,NULL,0,NULL);if(thread)CloseHandle(thread);
    }
    return TRUE;
}
