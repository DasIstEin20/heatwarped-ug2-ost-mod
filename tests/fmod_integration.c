#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
static HMODULE fmod_module;
#define API(name,ret,params) typedef ret (*type_##name) params; static type_##name name
API(FMOD_Studio_System_Create,int,(void **,unsigned));
API(FMOD_Studio_System_GetCoreSystem,int,(void *,void **));
API(FMOD_System_SetOutput,int,(void *,int));
API(FMOD_Studio_System_Initialize,int,(void *,int,unsigned,unsigned,void *));
API(FMOD_Studio_System_LoadBankFile,int,(void *,const char *,unsigned,void **));
API(FMOD_Studio_System_GetEvent,int,(void *,const char *,void **));
API(FMOD_Studio_EventDescription_CreateInstance,int,(void *,void **));
API(FMOD_Studio_EventInstance_SetVolume,int,(void *,float));
API(FMOD_Studio_EventInstance_GetVolume,int,(void *,float *,float *));
API(FMOD_Studio_EventInstance_Start,int,(void *));
API(FMOD_Studio_EventInstance_Stop,int,(void *,int));
API(FMOD_Studio_System_Update,int,(void *));
API(FMOD_Studio_System_Release,int,(void *));
#define LOAD(name) name=(type_##name)(void *)GetProcAddress(fmod_module,#name);if(!name){printf("Missing %s\n",#name);return 2;}
#define CHECK(expr) do{int result=(expr);if(result){printf("FAIL line %d: %s = %d\n",__LINE__,#expr,result);return 3;}}while(0)
static int expect_context(const char *wanted) {
    FILE *fp=fopen("CustomMusic/now-playing.txt","rb");if(!fp)return 0;
    char state[100],context[200],title[2000];
    if(!fgets(state,100,fp)||!fgets(context,200,fp)||!fgets(title,2000,fp)){fclose(fp);return 0;}
    fclose(fp);printf("Status: %s / %s / %s",state,context,title);
    return strstr(state,"Playing") && strstr(context,wanted) && strlen(title)>3;
}
static void ticks(void *studio,int count) {
    for(int i=0;i<count;i++){FMOD_Studio_System_Update(studio);Sleep(16);}
}
static char *read_config(size_t *len) {
    FILE *fp=fopen("CustomMusic/playlists.ini","rb");fseek(fp,0,SEEK_END);*len=ftell(fp);rewind(fp);
    char *p=calloc(*len+4,1);fread(p,1,*len,fp);fclose(fp);return p;
}
static void write_config(const char *p,size_t len) {
    FILE *fp=fopen("CustomMusic/playlists.ini","wb");fwrite(p,1,len,fp);fclose(fp);
}
int main(void) {
    wchar_t root[32768];GetModuleFileNameW(NULL,root,32768);
    wchar_t *slash=wcsrchr(root,L'\\');if(!slash)return 1;*slash=0;
    if(!SetCurrentDirectoryW(root))return 1;
    fmod_module=LoadLibraryW(L"Heatwarped_Data\\Plugins\\x86_64\\fmodstudio.dll");
    if(!fmod_module)return 1;
    LOAD(FMOD_Studio_System_Create);LOAD(FMOD_Studio_System_GetCoreSystem);LOAD(FMOD_System_SetOutput);
    LOAD(FMOD_Studio_System_Initialize);LOAD(FMOD_Studio_System_LoadBankFile);LOAD(FMOD_Studio_System_GetEvent);
    LOAD(FMOD_Studio_EventDescription_CreateInstance);LOAD(FMOD_Studio_EventInstance_SetVolume);LOAD(FMOD_Studio_EventInstance_GetVolume);
    LOAD(FMOD_Studio_EventInstance_Start);LOAD(FMOD_Studio_EventInstance_Stop);LOAD(FMOD_Studio_System_Update);LOAD(FMOD_Studio_System_Release);
    void *studio=NULL,*core=NULL,*bank=NULL,*description=NULL,*menu=NULL,*drive=NULL;
    CHECK(FMOD_Studio_System_Create(&studio,0x2020b));CHECK(FMOD_Studio_System_GetCoreSystem(studio,&core));
    CHECK(FMOD_System_SetOutput(core,2));CHECK(FMOD_Studio_System_Initialize(studio,128,0,0,NULL));
    CHECK(FMOD_Studio_System_LoadBankFile(studio,"Heatwarped_Data/StreamingAssets/Master.strings.bank",0,&bank));
    CHECK(FMOD_Studio_System_LoadBankFile(studio,"Heatwarped_Data/StreamingAssets/Master.bank",0,&bank));
    CHECK(FMOD_Studio_System_GetEvent(studio,"event:/Music/Carbon",&description));
    CHECK(FMOD_Studio_EventDescription_CreateInstance(description,&menu));CHECK(FMOD_Studio_EventInstance_SetVolume(menu,.7f));
    CHECK(FMOD_Studio_EventInstance_Start(menu));ticks(studio,10);
    /* Deliberately load after the menu event has started: exercise startup recovery. */
    if(!LoadLibraryW(L".\\version.dll"))return 4;
    Sleep(500);ticks(studio,140);
    if(!expect_context("Menu")){puts("FAIL: menu startup recovery");return 5;}
    float volume=0,final_volume=0;
    CHECK(FMOD_Studio_EventInstance_GetVolume(menu,&volume,&final_volume));
    if(fabsf(volume)>0.001f){printf("FAIL: original menu not muted: %f\n",volume);return 6;}
    CHECK(FMOD_Studio_EventInstance_Stop(menu,1));
    CHECK(FMOD_Studio_System_GetEvent(studio,"event:/Music/Sirens",&description));
    CHECK(FMOD_Studio_EventDescription_CreateInstance(description,&drive));CHECK(FMOD_Studio_EventInstance_SetVolume(drive,.4f));
    CHECK(FMOD_Studio_EventInstance_Start(drive));ticks(studio,100);
    if(!expect_context("Driving / racing")){puts("FAIL: driving context");return 7;}
    CHECK(FMOD_Studio_EventInstance_GetVolume(drive,&volume,&final_volume));
    if(fabsf(volume)>0.001f){puts("FAIL: original driving not muted");return 8;}
    size_t len=0;char *original=read_config(&len),*modified=calloc(len+4,1);memcpy(modified,original,len);
    wchar_t *wide=(wchar_t *)(modified+2);wchar_t *setting=wcsstr(wide,L"Enabled=1");
    if(!setting)return 9;
    setting[8]=L'0';write_config(modified,len);ticks(studio,100);
    CHECK(FMOD_Studio_EventInstance_GetVolume(drive,&volume,&final_volume));
    write_config(original,len);free(original);free(modified);ticks(studio,100);
    if(fabsf(volume-.4f)>0.001f){printf("FAIL: fallback volume %f\n",volume);return 10;}
    if(!expect_context("Driving / racing"))return 11;
    CHECK(FMOD_Studio_EventInstance_Stop(drive,1));ticks(studio,75);
    CHECK(FMOD_Studio_System_Release(studio));
    puts("PASS: startup recovery, menu, driving, original mute, disabled fallback, live reload, stop, shutdown.");
    return 0;
}

