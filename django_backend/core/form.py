from django.forms import ModelForm, Form
from django.forms import DateField, CharField, ChoiceField, TextInput


class masterAdvancedSearch(Form):
	name = CharField(required=False)
	element_id = CharField(required=False)
