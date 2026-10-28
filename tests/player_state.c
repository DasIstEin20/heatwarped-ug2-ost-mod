/* Deterministic audio/state tests; no game, graphics device or audio output. */
#include "../native/music_proxy.c"
#include <math.h>
#define REQUIRE(expr) do {if(!(expr)){printf("FAIL line %d: %s\n",__LINE__,#expr);return 1;}} while(0)
static int playing=1,starts,stops,playlist_calls;
static int mock_stream(void *system,const char *file,unsigned mode,void *extra,void **out) {
    (void)system;(void)file;(void)mode;(void)extra;*out=(void *)2;return 0;
}
static int mock_play(void *system,void *stream,void *group,int paused,void **out) {
    (void)system;(void)stream;(void)group;(void)paused;*out=(void *)3;playing=1;starts++;return 0;
}
static int mock_one(void *object) {(void)object;return 0;}
static int mock_stop(void *object) {(void)object;stops++;return 0;}
static int mock_volume(void *object,float volume) {(void)object;(void)volume;return 0;}
static int mock_pause(void *object,int paused) {(void)object;(void)paused;return 0;}
static int mock_playing(void *object,int *out) {(void)object;*out=playing;return 0;}
static void mock_playlist(void *object,void *name,void *method) {(void)object;(void)name;(void)method;playlist_calls++;}
static int mock_length(void *text) {return (int)wcslen(text);}
static wchar_t *mock_chars(void *text) {return text;}
static void tick(void) {
    last_config_check=last_recover=GetTickCount64();hooked_update((void *)1);
}
int main(void) {
    wchar_t temp[MAX_PATH],folder[MAX_PATH];GetTempPathW(MAX_PATH,temp);
    swprintf(folder,MAX_PATH,L"%lsHeatwarpedPlayerTest-%lu",temp,GetCurrentProcessId());CreateDirectoryW(folder,NULL);
    swprintf(config_path,1200,L"%ls\\playlists.ini",folder);swprintf(log_path,1200,L"%ls\\music.log",folder);
    swprintf(status_path,1200,L"%ls\\status.txt",folder);wcscpy(game_dir,folder);
    create_stream=mock_stream;play_sound=mock_play;stop_channel=mock_stop;release_sound=mock_one;
    set_channel_volume=mock_volume;set_channel_paused=mock_pause;is_playing=mock_playing;
    original_update=mock_one;original_volume=mock_volume;core=active_instance=(void *)1;
    buses_logged=group_routed=1;context=2;enabled=shuffle=1;track_count=3;srand(123);
    for(int i=0;i<3;i++){swprintf(tracks[i].file,1024,L"Tracks\\%d.wav",i);swprintf(tracks[i].title,512,L"Artist - Track %d",i);tracks[i].drive=1;}
    for(int i=0;i<40;i++) {
        int previous=current_track;next_track();REQUIRE(current_track>=0 && current_track!=previous);
    }
    ULONGLONG end=overlay_toast_until;
    REQUIRE(end-GetTickCount64()<=4000 && end-GetTickCount64()>=3950);
    REQUIRE(overlay_alpha_at(end-4000)==1 && overlay_alpha_at(end-800)==1);
    REQUIRE(fabsf(overlay_alpha_at(end-400)-.5f)<.001f);
    REQUIRE(overlay_alpha_at(end-200)<overlay_alpha_at(end-400));
    REQUIRE(overlay_alpha_at(end)==0 && overlay_alpha_at(end+1)==0);
    int previous=current_track,before=starts;playing=0;tick();
    REQUIRE(starts==before+1 && current_track!=previous && overlay_toast_until>=end);
    previous=current_track;before=starts;InterlockedExchange(&skip_requested,1);tick();
    REQUIRE(starts==before+1 && current_track!=previous);tick();REQUIRE(starts==before+1);
    il_string_length=mock_length;il_string_chars=mock_chars;ui_original_playlist=mock_playlist;
    for(int i=0;i<2;i++) {
        previous=current_track;before=starts;overlay_playlist(NULL,L"Racing",NULL);tick();
        REQUIRE(starts==before+1 && current_track!=previous && !race_requested);
        tick();REQUIRE(starts==before+1);
    }
    before=starts;overlay_playlist(NULL,L"FreeRoam",NULL);tick();REQUIRE(starts==before);
    REQUIRE(playlist_calls==3);
    context=1;overlay_playlist(NULL,L"Racing",NULL);tick();REQUIRE(race_requested);
    context=2;tick();REQUIRE(!race_requested && starts==before+1);
    /* Round-trip the exact file format shared with the offline mixer. */
    gain=.37f;tracks[1].menu=1;REQUIRE(save_config());
    REQUIRE(track_count==3 && tracks[1].menu && fabsf(gain-.37f)<.001f);
    int drive=tracks[current_track].drive;tracks[current_track].drive=0;REQUIRE(save_config());REQUIRE(!channel);
    tracks[last_track[2]].drive=drive;REQUIRE(save_config());tick();REQUIRE(channel);
    enabled=0;REQUIRE(save_config());REQUIRE(!channel);before=starts;tick();REQUIRE(starts==before);
    end_track();DeleteFileW(config_path);DeleteFileW(log_path);DeleteFileW(status_path);RemoveDirectoryW(folder);
    printf("PASS: shuffle, no immediate repeats, automatic next, F9, race start/restart, single advance, 4s toast, smooth fade, UTF-16 save/reload, eligibility, disabled fallback (%d streams, %d stops).\n",starts,stops);
    return 0;
}
