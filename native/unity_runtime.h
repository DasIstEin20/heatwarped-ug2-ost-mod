/* Small, dynamically resolved IL2CPP bridge. No game addresses or engine binaries. */
#define ILAPI(ret,name,args) static ret (*il_##name) args
ILAPI(void *,domain_get,(void));
ILAPI(void **,domain_get_assemblies,(void *,size_t *));
ILAPI(void *,assembly_get_image,(void *));
ILAPI(void *,class_from_name,(void *,const char *,const char *));
ILAPI(void *,class_get_method_from_name,(void *,const char *,int));
ILAPI(void *,class_get_methods,(void *,void **));
ILAPI(void *,class_get_type,(void *));
ILAPI(void *,type_get_object,(void *));
ILAPI(char *,type_get_name,(void *));
ILAPI(const char *,method_get_name,(void *));
ILAPI(unsigned,method_get_param_count,(void *));
ILAPI(void *,method_get_param,(void *,unsigned));
ILAPI(void *,object_new,(void *));
ILAPI(void *,object_unbox,(void *));
ILAPI(void *,runtime_invoke,(void *,void *,void **,void **));
ILAPI(void *,string_new,(const char *));
ILAPI(void *,string_new_utf16,(const wchar_t *,int));
ILAPI(int,string_length,(void *));
ILAPI(wchar_t *,string_chars,(void *));
ILAPI(void *,thread_current,(void));
ILAPI(uintptr_t,gchandle_new,(void *,unsigned char));
ILAPI(void *,gchandle_get_target,(uintptr_t));
ILAPI(void,gchandle_free,(uintptr_t));
ILAPI(void,format_exception,(void *,char *,int));
ILAPI(void,free,(void *));
#undef ILAPI

static int unity_api_ready;
static void *unity_export(HMODULE module,const char *name) {
    void *address=(void *)GetProcAddress(module,name);if(address)return address;
    /* Unity 6000.4.1f1 renames its public IL2CPP exports. This name map is
       restricted to the tested demo build; no executable offsets are used. */
    IMAGE_DOS_HEADER *dos=(void *)module;
    IMAGE_NT_HEADERS *pe=(void *)((char *)module+dos->e_lfanew);
    if(pe->FileHeader.TimeDateStamp!=0x6a8a3a3e || pe->OptionalHeader.SizeOfImage!=0x3bd7000)return NULL;
    static const struct {const char *name,*export;} map[]={
        {"domain_get","QmVPhGkDypF"},{"domain_get_assemblies","ReNTqvHFvqj"},
        {"assembly_get_image","TKIhIpYuGSi"},{"class_from_name","UFKu__BiXnc"},
        {"class_get_method_from_name","fZGcbwjdkAt"},{"class_get_methods","nLvDrq_dRtw"},
        {"class_get_type","hjmlTSrZbLG"},{"type_get_object","wWVkwkoMxSw"},
        {"type_get_name","onczthGVUIe"},{"method_get_name","zVeuF_lsnIA"},
        {"method_get_param_count","wOdpZZuLsXS"},{"method_get_param","ukkMkYYpzhB"},
        {"object_new","OsKYUjFdKST"},
        {"object_unbox","XMsShjnBISz"},{"runtime_invoke","cPBZyrapAxb"},
        {"string_new","miwDrICdcDP"},{"string_new_utf16","mpYDVXWlPdp"},
        {"string_length","LAmHscVkTuP"},{"string_chars","TZJFOWWTSGn"},
        {"thread_current","eGjtUMqKEMs"},{"gchandle_new","ypQvhWOZOqD"},
        {"gchandle_get_target","VxlAMbBuoxj"},{"gchandle_free","ROSWnwGRhbf"},
        {"format_exception","tWnqO_MDlfn"},{"free","iItYjSrzQLv"}
    };
    for(unsigned i=0;i<sizeof(map)/sizeof(map[0]);i++)
        if(!strcmp(name+7,map[i].name))return (void *)GetProcAddress(module,map[i].export);
    return NULL;
}
static int unity_api_init(void) {
    if(unity_api_ready)return 1;
    HMODULE module=GetModuleHandleW(L"GameAssembly.dll");if(!module)return 0;
#define ILLOAD(name) il_##name=unity_export(module,"il2cpp_" #name);if(!il_##name)return 0
    ILLOAD(domain_get);ILLOAD(domain_get_assemblies);ILLOAD(assembly_get_image);
    ILLOAD(class_from_name);ILLOAD(class_get_method_from_name);ILLOAD(class_get_methods);
    ILLOAD(class_get_type);
    ILLOAD(type_get_object);ILLOAD(type_get_name);ILLOAD(method_get_name);
    ILLOAD(method_get_param_count);ILLOAD(method_get_param);
    ILLOAD(object_new);ILLOAD(object_unbox);ILLOAD(runtime_invoke);ILLOAD(string_new);
    ILLOAD(string_new_utf16);ILLOAD(thread_current);ILLOAD(gchandle_new);
    ILLOAD(string_length);ILLOAD(string_chars);
    ILLOAD(gchandle_get_target);ILLOAD(gchandle_free);ILLOAD(format_exception);ILLOAD(free);
#undef ILLOAD
    unity_api_ready=1;return 1;
}
static void *unity_class(const char *space,const char *name) {
    size_t count=0;void **assemblies=il_domain_get_assemblies(il_domain_get(),&count);
    for(size_t i=0;i<count;i++) {
        void *klass=il_class_from_name(il_assembly_get_image(assemblies[i]),space,name);
        if(klass)return klass;
    }
    return NULL;
}
/* Select overloads by parameter types, since IL2CPP can strip unused overloads. */
static void *unity_method(void *klass,const char *name,int count,const char *first_type) {
    if(!klass)return NULL;
    if(!first_type)return il_class_get_method_from_name(klass,name,count);
    void *iter=NULL,*method;
    while((method=il_class_get_methods(klass,&iter))) {
        if(strcmp(il_method_get_name(method),name) || il_method_get_param_count(method)!=(unsigned)count)continue;
        char *type=il_type_get_name(il_method_get_param(method,0));
        int match=type && !strcmp(type,first_type);il_free(type);
        if(match)return method;
    }
    return NULL;
}
static void *unity_call(void *method,void *object,void **args) {
    if(!method)return NULL;
    void *exception=NULL,*result=il_runtime_invoke(method,object,args,&exception);
    if(exception) {
        static unsigned errors;
        if(errors++<8) {char message[1200];il_format_exception(exception,message,sizeof(message));log_line("Unity call %s: %s",il_method_get_name(method),message);}
        return NULL;
    }
    return result;
}
