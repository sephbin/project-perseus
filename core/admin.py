from django.contrib import admin

from core.models import Element, Parameter


class ElementAdmin(admin.ModelAdmin):
    pass


class ParameterAdmin(admin.ModelAdmin):
    pass


admin.site.register(Element, ElementAdmin)
admin.site.register(Parameter, ParameterAdmin)
