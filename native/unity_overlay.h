/* Unity draws this panel in-game on Vulkan, D3D11 and D3D12. Managed objects
   are rooted with GC handles. Only Unity's main thread calls engine APIs. */
#include "unity_runtime.h"
typedef struct {float x,y,w,h;} UiRect;
typedef struct {float x,y;} UiVec;
typedef struct {float r,g,b,a;} UiColor;
static LONG overlay_toggle_requested,race_requested;
static int overlay_ready,overlay_open,overlay_failed,overlay_dragging,overlay_volume_drag,unity_game_hooks_checked;
static int overlay_scroll,overlay_filter,overlay_save_failed;
static float overlay_x=190,overlay_y=60,overlay_scale=1,overlay_drag_x,overlay_drag_y;
static float overlay_opacity=1;
static ULONGLONG overlay_toast_until,overlay_saved_until;
static uintptr_t overlay_object,overlay_style,overlay_texture,overlay_event_system;
static unsigned char overlay_old_paused,overlay_old_ignore,overlay_old_cursor,overlay_old_event_enabled;
static int overlay_old_lock;
static void *ui_gui,*ui_event_class,*ui_screen,*ui_cursor,*ui_time,*ui_input,*ui_behaviour;
static void *ui_label,*ui_draw,*ui_color,*ui_get_color,*ui_font_size,*ui_font_style;
static void *ui_current_event,*ui_event_type,*ui_mouse,*ui_delta,*ui_event_use,*ui_key;
static void *ui_width,*ui_height,*ui_paused_get,*ui_paused_set,*ui_ignore_get,*ui_ignore_set;
static void *ui_cursor_visible_get,*ui_cursor_visible_set,*ui_cursor_lock_get,*ui_cursor_lock_set;
static void *ui_events_current,*ui_enabled_get,*ui_enabled_set,*ui_own_helper;
static void (*ui_original_ongui)(void *,void *);
static void (*ui_original_playlist)(void *,void *,void *);
static void (*ui_original_notification)(void *,void *,void *);
static void overlay_notify(void) {overlay_toast_until=GetTickCount64()+4000;}
static float overlay_alpha_at(ULONGLONG now) {
    if(now>=overlay_toast_until)return 0;
    float remaining=(float)(overlay_toast_until-now)/800.f;
    return remaining<1?remaining*remaining*(3-2*remaining):1;
}
static int ui_integer(void *method,void *object) {
    void *result=unity_call(method,object,NULL);return result?*(int *)il_object_unbox(result):0;
}
static unsigned char ui_boolean(void *method,void *object) {
    void *result=unity_call(method,object,NULL);return result?*(unsigned char *)il_object_unbox(result):0;
}
static void ui_set_bool(void *method,void *object,unsigned char value) {
    void *args[]={&value};unity_call(method,object,args);
}
static void ui_set_int(void *method,void *object,int value) {
    void *args[]={&value};unity_call(method,object,args);
}
static void *ui_object(uintptr_t handle) {return handle?il_gchandle_get_target(handle):NULL;}
static void ui_set_color(UiColor color) {void *args[]={&color};unity_call(ui_color,NULL,args);}
static void ui_rect(float x,float y,float w,float h,UiColor color) {
    UiRect rect={x*overlay_scale,y*overlay_scale,w*overlay_scale,h*overlay_scale};
    void *args[]={&rect,ui_object(overlay_texture)};
    color.a*=overlay_opacity;ui_set_color(color);unity_call(ui_draw,NULL,args);
}
static void ui_text(float x,float y,float w,float h,const wchar_t *text,int size,int italic,UiColor color) {
    void *style=ui_object(overlay_style);
    ui_set_int(ui_font_size,style,(int)(size*overlay_scale));ui_set_int(ui_font_style,style,italic?2:0);
    UiRect rect={x*overlay_scale,y*overlay_scale,w*overlay_scale,h*overlay_scale};
    void *args[]={&rect,il_string_new_utf16(text,(int)wcslen(text)),style};
    color.a*=overlay_opacity;ui_set_color(color);unity_call(ui_label,NULL,args);
}
static const UiColor ui_white={.94f,.96f,.90f,1},ui_green={.68f,.83f,.38f,1};
static const UiColor ui_muted={.61f,.67f,.66f,1},ui_dark={.035f,.05f,.045f,.97f};
static UiVec overlay_mouse;
static int overlay_click,overlay_mouse_down,overlay_mouse_up;
static int ui_hit(float x,float y,float w,float h) {
    return overlay_mouse.x>=x && overlay_mouse.x<x+w && overlay_mouse.y>=y && overlay_mouse.y<y+h;
}
static int ui_button(float x,float y,float w,const wchar_t *label) {
    int hover=ui_hit(x,y,w,30);
    ui_rect(x,y,w,30,hover?(UiColor){.27f,.34f,.21f,1}:(UiColor){.12f,.16f,.13f,1});
    ui_text(x+9,y+3,w-16,26,label,15,0,ui_white);
    return overlay_click && hover;
}
static int ui_checkbox(float x,float y,int value) {
    ui_rect(x,y,22,22,(UiColor){.25f,.31f,.24f,1});ui_rect(x+2,y+2,18,18,value?ui_green:ui_dark);
    if(value)ui_text(x+4,y-2,20,25,L"+",19,0,ui_dark);
    return overlay_click && ui_hit(x-4,y-4,45,30);
}
static void overlay_set_open(int open) {
    if(open==overlay_open)return;
    if(open) {
        overlay_old_paused=ui_boolean(ui_paused_get,NULL);overlay_old_ignore=ui_boolean(ui_ignore_get,NULL);
        overlay_old_cursor=ui_boolean(ui_cursor_visible_get,NULL);overlay_old_lock=ui_integer(ui_cursor_lock_get,NULL);
        void *events=unity_call(ui_events_current,NULL,NULL);
        if(events) {
            overlay_event_system=il_gchandle_new(events,0);overlay_old_event_enabled=ui_boolean(ui_enabled_get,events);
            ui_set_bool(ui_enabled_set,events,0);
        }
        ui_set_bool(ui_paused_set,NULL,1);ui_set_bool(ui_ignore_set,NULL,1);
    } else {
        ui_set_bool(ui_paused_set,NULL,overlay_old_paused);ui_set_bool(ui_ignore_set,NULL,overlay_old_ignore);
        ui_set_bool(ui_cursor_visible_set,NULL,overlay_old_cursor);ui_set_int(ui_cursor_lock_set,NULL,overlay_old_lock);
        if(overlay_event_system) {
            ui_set_bool(ui_enabled_set,ui_object(overlay_event_system),overlay_old_event_enabled);
            il_gchandle_free(overlay_event_system);overlay_event_system=0;
        }
        overlay_dragging=overlay_volume_drag=0;
    }
    overlay_open=open;log_line("In-game music panel %s",open?"opened":"closed");
}
static void overlay_commit(void) {
    if(!save_config()){overlay_save_failed=1;return;}
    overlay_save_failed=0;overlay_saved_until=GetTickCount64()+1800;
}
static void overlay_toast(void) {
    ULONGLONG now=GetTickCount64();
    if(context!=2 || !channel || current_track<0 || now>=overlay_toast_until)return;
    overlay_opacity=overlay_alpha_at(now);
    float x=28,y=28,w=445,h=120;
    ui_rect(x,y+16,w,h-16,(UiColor){.025f,.035f,.02f,.86f});ui_rect(x,y,228,16,(UiColor){.025f,.035f,.02f,.86f});
    ui_rect(x,y,220,2,ui_green);ui_rect(x,y,2,h,ui_green);
    for(int i=0;i<8;i++)ui_rect(x+220+i*2,y+i*2,3,2,ui_green);
    ui_rect(x+236,y+16,w-236,2,ui_green);ui_rect(x+w-2,y+16,2,h-16,ui_green);ui_rect(x,y+h-2,w,2,ui_green);
    ui_rect(x+14,y+14,27,27,(UiColor){.18f,.24f,.10f,1});
    for(int i=0;i<8;i++)ui_rect(x+22+i,y+21+i*.5f,1,16-i,ui_green);
    ui_text(x+50,y+10,w-65,26,L"HEATWARPED TRAX",15,1,ui_green);
    wchar_t artist[512],title[512];wcsncpy(artist,tracks[current_track].title,511);artist[511]=0;
    wchar_t *split=wcsstr(artist,L" - ");
    if(split){*split=0;swprintf(title,512,L"%ls",split+3);}
    else {swprintf(title,512,L"%ls",artist);wcscpy(artist,L"CUSTOM MUSIC");}
    ui_text(x+15,y+43,w-30,31,artist,23,1,ui_white);ui_text(x+15,y+76,w-30,31,title,22,1,ui_white);
    overlay_opacity=1;
}
static void overlay_panel(void *event,int type) {
    float x=overlay_x,y=overlay_y,w=900,h=600;int dirty=0;
    if(type==4 && ui_integer(ui_key,event)==27){overlay_set_open(0);unity_call(ui_event_use,event,NULL);return;}
    if(overlay_click && ui_hit(x,y,w-60,44)){overlay_dragging=1;overlay_drag_x=overlay_mouse.x-x;overlay_drag_y=overlay_mouse.y-y;}
    if(overlay_dragging && (type==3 || overlay_mouse_down)) {
        overlay_x=overlay_mouse.x-overlay_drag_x;overlay_y=overlay_mouse.y-overlay_drag_y;
        float sw=ui_integer(ui_width,NULL)/overlay_scale,sh=ui_integer(ui_height,NULL)/overlay_scale;
        if(overlay_x<0)overlay_x=0;
        if(overlay_y<0)overlay_y=0;
        if(overlay_x>sw-w)overlay_x=sw-w;
        if(overlay_y>sh-44)overlay_y=sh-44;
        x=overlay_x;y=overlay_y;
    }
    if(overlay_mouse_up)overlay_dragging=overlay_volume_drag=0;
    ui_rect(x-1,y-1,w+2,h+2,ui_green);ui_rect(x,y,w,h,ui_dark);ui_rect(x,y,w,44,(UiColor){.13f,.19f,.10f,1});
    ui_text(x+18,y+8,730,30,L"HEATWARPED TRAX  /  MUSIC SETTINGS",21,1,ui_white);
    if(ui_button(x+w-49,y+7,35,L"X")){overlay_set_open(0);unity_call(ui_event_use,event,NULL);return;}
    ui_text(x+18,y+53,w-36,23,L"F8 / Esc: close    |    F9: next track    |    Scroll: browse songs",15,0,ui_muted);
    if(ui_checkbox(x+20,y+91,enabled)){enabled=!enabled;dirty=1;}
    ui_text(x+49,y+88,170,28,L"Custom music",17,0,ui_white);
    if(ui_checkbox(x+234,y+91,shuffle)){shuffle=!shuffle;dirty=1;}
    ui_text(x+263,y+88,140,28,L"Shuffle",17,0,ui_white);
    wchar_t volume[64];swprintf(volume,64,L"Volume  %d%%",(int)(gain*100+.5f));ui_text(x+410,y+88,140,28,volume,17,0,ui_white);
    float vx=x+554,vy=y+101,vw=200;
    ui_rect(vx,vy-3,vw,6,(UiColor){.24f,.29f,.23f,1});ui_rect(vx,vy-3,vw*gain,6,ui_green);ui_rect(vx+vw*gain-4,vy-8,8,16,ui_white);
    if(overlay_click && ui_hit(vx-8,vy-16,vw+16,32))overlay_volume_drag=1;
    if(overlay_volume_drag && (overlay_click || type==3)) {
        float value=(overlay_mouse.x-vx)/vw;if(value<0)value=0;if(value>1)value=1;
        if(value!=gain){gain=value;dirty=1;}
    }
    if(ui_button(x+775,y+86,105,L"Next >"))InterlockedExchange(&skip_requested,1);
    const wchar_t *filters[]={L"All tracks",L"Menu",L"Driving"};
    for(int i=0;i<3;i++)if(ui_button(x+20+i*125,y+131,115,filters[i])){overlay_filter=i;overlay_scroll=0;}
    int filtered[MAX_TRACKS],count=0,menu_count=0,drive_count=0;
    for(int i=0;i<track_count;i++) {
        menu_count+=tracks[i].menu;drive_count+=tracks[i].drive;
        if(!overlay_filter || (overlay_filter==1?tracks[i].menu:tracks[i].drive))filtered[count++]=i;
    }
    wchar_t counts[100];swprintf(counts,100,L"%d songs    Menu: %d    Driving: %d",track_count,menu_count,drive_count);
    ui_text(x+440,y+135,430,25,counts,16,0,ui_green);
    ui_text(x+24,y+176,600,26,L"ARTIST / TRACK",15,0,ui_muted);ui_text(x+691,y+176,75,26,L"MENU",15,0,ui_muted);ui_text(x+781,y+176,110,26,L"DRIVING",15,0,ui_muted);
    enum {page=9};
    if(type==6 && ui_hit(x,y+169,w,320)) {
        void *boxed=unity_call(ui_delta,event,NULL);
        if(boxed){UiVec delta=*(UiVec *)il_object_unbox(boxed);overlay_scroll+=delta.y>0?3:delta.y<0?-3:0;}
    }
    if(overlay_scroll>count-page)overlay_scroll=count-page;
    if(overlay_scroll<0)overlay_scroll=0;
    for(int row=0;row<page && overlay_scroll+row<count;row++) {
        int i=filtered[overlay_scroll+row];float ry=y+205+row*31;
        if(row%2==0)ui_rect(x+16,ry-2,w-32,31,(UiColor){.075f,.10f,.08f,1});
        if(i==current_track)ui_rect(x+17,ry,3,25,ui_green);
        ui_text(x+26,ry,650,28,tracks[i].title,17,0,i==current_track?ui_green:ui_white);
        if(ui_checkbox(x+705,ry+2,tracks[i].menu)){tracks[i].menu=!tracks[i].menu;dirty=1;}
        if(ui_checkbox(x+809,ry+2,tracks[i].drive)){tracks[i].drive=!tracks[i].drive;dirty=1;}
    }
    if(!count)ui_text(x+28,y+230,800,35,L"This playlist is empty. Select All tracks to add songs.",18,0,ui_muted);
    if(ui_button(x+20,y+492,108,L"< Previous"))overlay_scroll-=page;
    if(ui_button(x+137,y+492,90,L"Next >"))overlay_scroll+=page;
    wchar_t range[90];swprintf(range,90,L"%d - %d / %d",count?overlay_scroll+1:0,overlay_scroll+page<count?overlay_scroll+page:count,count);
    ui_text(x+244,y+496,180,25,range,16,0,ui_muted);
    const wchar_t *bulk[]={L"Menu: all",L"Menu: clear",L"Drive: all",L"Drive: clear"};
    for(int b=0;b<4;b++)if(ui_button(x+414+b*116,y+492,108,bulk[b])) {
        for(int i=0;i<track_count;i++){if(b<2)tracks[i].menu=b==0;else tracks[i].drive=b==2;}dirty=1;
    }
    ui_rect(x+18,y+534,w-36,1,(UiColor){.22f,.28f,.19f,1});
    ui_text(x+22,y+543,850,26,current_track>=0?tracks[current_track].title:L"Waiting for music",17,1,ui_white);
    const wchar_t *saved=overlay_save_failed?L"Could not save playlists.ini. Check folder permissions.":GetTickCount64()<overlay_saved_until?L"Saved. Changes apply immediately.":L"Settings save automatically. Empty playlists use the original soundtrack.";
    ui_text(x+22,y+574,850,23,saved,14,0,overlay_save_failed?(UiColor){1,.45f,.35f,1}:ui_muted);
    if(dirty)overlay_commit();
    if(type!=7 && type!=8 && type!=12)unity_call(ui_event_use,event,NULL);
}
static void overlay_ongui(void *object,void *method) {
    if(object!=ui_own_helper){ui_original_ongui(object,method);return;}
    if(!overlay_ready)return;
    int sw=ui_integer(ui_width,NULL),sh=ui_integer(ui_height,NULL);
    overlay_scale=(float)sw/1280;if((float)sh/720<overlay_scale)overlay_scale=(float)sh/720;
    if(overlay_scale<=0)return;
    void *event=unity_call(ui_current_event,NULL,NULL);if(!event)return;
    int type=ui_integer(ui_event_type,event);if(!overlay_open && type!=7)return;
    void *boxed=unity_call(ui_mouse,event,NULL);
    if(boxed){overlay_mouse=*(UiVec *)il_object_unbox(boxed);overlay_mouse.x/=overlay_scale;overlay_mouse.y/=overlay_scale;}
    overlay_click=type==0;overlay_mouse_down=type==0 || type==3;overlay_mouse_up=type==1;
    UiColor saved=ui_white;boxed=unity_call(ui_get_color,NULL,NULL);if(boxed)saved=*(UiColor *)il_object_unbox(boxed);
    if(overlay_open)overlay_panel(event,type);else overlay_toast();
    ui_set_color(saved);
}
static void overlay_playlist(void *object,void *name,void *method) {
    if(name && il_string_length(name)==6 && !wcsncmp(il_string_chars(name),L"Racing",6)) {
        InterlockedExchange(&race_requested,1);log_line("Race countdown: next custom track requested");
    } else InterlockedExchange(&race_requested,0);
    ui_original_playlist(object,name,method);
}
static void overlay_game_notification(void *object,void *track,void *method) {
    /* The original footer names the muted Heatwarped song. Our own toast
       displays the custom song; keep the footer for the original fallback. */
    if(overlay_ready && has_playlist(context))return;
    ui_original_notification(object,track,method);
}
static int overlay_hook(void *method,void *hook,void **original) {
    if(!method || !*(void **)method)return 0;
    void *address=*(void **)method;MEMORY_BASIC_INFORMATION info;
    if(!VirtualQuery(address,&info,sizeof(info)) || !(info.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE)))return 0;
    if(MH_CreateHook(address,hook,original)!=MH_OK)return 0;
    if(MH_EnableHook(address)!=MH_OK){MH_RemoveHook(address);return 0;}return 1;
}
static int overlay_init(void) {
    ui_gui=unity_class("UnityEngine","GUI");ui_event_class=unity_class("UnityEngine","Event");
    ui_screen=unity_class("UnityEngine","Screen");ui_cursor=unity_class("UnityEngine","Cursor");
    ui_time=unity_class("","TimeManager");ui_input=unity_class("","LuaInputAPI");ui_behaviour=unity_class("UnityEngine","Behaviour");
#define UIM(target,klass,name,n,type) target=unity_method(klass,name,n,type);if(!target){log_line("In-game UI unavailable: %s",name);return 0;}
    UIM(ui_label,ui_gui,"Label",3,NULL);UIM(ui_draw,ui_gui,"DrawTexture",2,NULL);
    UIM(ui_color,ui_gui,"set_color",1,NULL);UIM(ui_get_color,ui_gui,"get_color",0,NULL);
    void *style_class=unity_class("UnityEngine","GUIStyle");
    UIM(ui_font_size,style_class,"set_fontSize",1,NULL);UIM(ui_font_style,style_class,"set_fontStyle",1,NULL);
    UIM(ui_current_event,ui_event_class,"get_current",0,NULL);UIM(ui_event_type,ui_event_class,"get_type",0,NULL);
    UIM(ui_mouse,ui_event_class,"get_mousePosition",0,NULL);UIM(ui_delta,ui_event_class,"get_delta",0,NULL);
    UIM(ui_event_use,ui_event_class,"Use",0,NULL);UIM(ui_key,ui_event_class,"get_keyCode",0,NULL);
    UIM(ui_width,ui_screen,"get_width",0,NULL);UIM(ui_height,ui_screen,"get_height",0,NULL);
    UIM(ui_paused_get,ui_time,"get_IsPaused",0,NULL);UIM(ui_paused_set,ui_time,"set_IsPaused",1,NULL);
    UIM(ui_ignore_get,ui_input,"get_ignore_car_inputs",0,NULL);UIM(ui_ignore_set,ui_input,"set_ignore_car_inputs",1,NULL);
    UIM(ui_cursor_visible_get,ui_cursor,"get_visible",0,NULL);UIM(ui_cursor_visible_set,ui_cursor,"set_visible",1,NULL);
    UIM(ui_cursor_lock_get,ui_cursor,"get_lockState",0,NULL);UIM(ui_cursor_lock_set,ui_cursor,"set_lockState",1,NULL);
    void *events_class=unity_class("UnityEngine.EventSystems","EventSystem");
    UIM(ui_events_current,events_class,"get_current",0,NULL);UIM(ui_enabled_get,ui_behaviour,"get_enabled",0,NULL);UIM(ui_enabled_set,ui_behaviour,"set_enabled",1,NULL);
    void *game_class=unity_class("UnityEngine","GameObject"),*helper=unity_class("FMODUnity","FMODRuntimeManagerOnGUIHelper");
    void *ctor,*add,*keep,*ongui;
    UIM(ctor,game_class,".ctor",1,"System.String");UIM(add,game_class,"AddComponent",1,"System.Type");
    UIM(keep,unity_class("UnityEngine","Object"),"DontDestroyOnLoad",1,NULL);UIM(ongui,helper,"OnGUI",0,NULL);
    void *style=il_object_new(style_class);unity_call(unity_method(style_class,".ctor",0,NULL),style,NULL);overlay_style=il_gchandle_new(style,0);
    void *state=unity_call(unity_method(style_class,"get_normal",0,NULL),style,NULL),*color_args[]={(void *)&ui_white};
    unity_call(unity_method(unity_class("UnityEngine","GUIStyleState"),"set_textColor",1,NULL),state,color_args);
    void *texture=unity_call(unity_method(unity_class("UnityEngine","Texture2D"),"get_whiteTexture",0,NULL),NULL,NULL);
    if(!texture)return 0;
    overlay_texture=il_gchandle_new(texture,0);
    if(!overlay_hook(ongui,overlay_ongui,(void **)&ui_original_ongui))return 0;
    void *object=il_object_new(game_class),*args[]={il_string_new("Heatwarped UG2 Music Overlay")};
    unity_call(ctor,object,args);overlay_object=il_gchandle_new(object,0);
    void *keep_args[]={object};unity_call(keep,NULL,keep_args);
    void *add_args[]={il_type_get_object(il_class_get_type(helper))};ui_own_helper=unity_call(add,object,add_args);if(!ui_own_helper)return 0;
    overlay_ready=1;log_line("In-game music overlay ready (Unity IMGUI); F8=settings, F9=next");return 1;
#undef UIM
}
static void unity_overlay_tick(void) {
    if(overlay_failed)return;
    if(!overlay_ready) {
        if(!unity_api_init() || !il_thread_current() || !il_domain_get())return;
        if(!unity_class("UnityEngine","GameObject"))return;
        if(!unity_game_hooks_checked) {
            unity_game_hooks_checked=1;
            void *playlist=unity_method(unity_class("","MusicManager"),"PlayPlaylist",1,"System.String");
            if(!overlay_hook(playlist,overlay_playlist,(void **)&ui_original_playlist))log_line("Race music hook unavailable");
            else log_line("Race countdown music hook installed");
            void *notification=unity_method(unity_class("","MusicNotification"),"Show",1,NULL);
            if(!overlay_hook(notification,overlay_game_notification,(void **)&ui_original_notification))log_line("Original music footer hook unavailable");
        }
        if(!overlay_init()){overlay_failed=1;log_line("In-game UI initialization failed; audio remains available");return;}
    }
    if(InterlockedExchange(&overlay_toggle_requested,0))overlay_set_open(!overlay_open);
    if(overlay_open){ui_set_bool(ui_cursor_visible_set,NULL,1);ui_set_int(ui_cursor_lock_set,NULL,0);}
    if(!(GetAsyncKeyState(VK_LBUTTON)&0x8000))overlay_dragging=overlay_volume_drag=0;
}
