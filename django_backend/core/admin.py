from django.contrib import admin

from core.models import *


class SourceAdmin(admin.ModelAdmin):
    list_display = ('unique_id', 'name')
    search_fields = ["unique_id","name"]
    pass

class ElementAdmin(admin.ModelAdmin):
    list_display = ('unique_id', 'name','created_at','updated_at','source_state')
    readonly_fields = ('parameter_dict',)
    search_fields = ["unique_id"]
    ordering = ['-updated_at',]
    pass


class ParameterAdmin(admin.ModelAdmin):
    list_display = ('id', 'name','value','value_type')
    search_fields = ["name","value"]
    pass


admin.site.register(Source, SourceAdmin)
admin.site.register(Element, ElementAdmin)
admin.site.register(Parameter, ParameterAdmin)
