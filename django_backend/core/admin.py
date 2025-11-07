import sys
from django.contrib import admin
from .models import *
from django_admin_search.admin import AdvancedSearchAdmin
from .form import *

class Parent_Admin(AdvancedSearchAdmin):
    search_form = masterAdvancedSearch
    list_display = ['name']
    list_editable = []
    list_display_links = ['name']
    search_fields = []
    readonly_fields = []
    list_filter = []
    actions = []
    save_on_top = True
    save_as = True
    list_per_page = 500



class Element_Admin(Parent_Admin):
    list_display = ['element_id']+Parent_Admin.list_display+['unique_id','updated_at','last_edited_by']
    list_display_links = ['element_id']
    list_filter = ('source_model',)
    readonly_fields = Parent_Admin.readonly_fields+['parameter_dict',]
    search_fields = Parent_Admin.search_fields+["unique_id","element_id"]
    ordering = ['-updated_at',]
    pass


class Event_Admin(Parent_Admin):
    list_display = ['id']+Parent_Admin.list_display+['user','event_type','description']
    list_display_links = ['id']
    list_filter = ('source_model',)
    ordering = ['-updated_at',]
    pass

class Source_Admin(Parent_Admin):
    list_display = ["id"]+Parent_Admin.list_display+["unique_id"]
    list_display_links = ['id']


class Through_SourceToUser_Admin(Parent_Admin):
    list_display = ['id']+['source_model', 'user_model', 'access', 'updated']
    list_display_links = ['id']
    list_filter = ('source_model',)
    ordering = ['-updated',]
    pass

for subclass in ParentModel.__subclasses__():
    try:
        try:
            adminName = str(subclass.__name__)+"_Admin"
            adminOb = getattr(sys.modules[__name__], adminName)
        except Exception as e:
            adminOb = Parent_Admin
        admin.site.register(subclass, adminOb)
    except: pass


